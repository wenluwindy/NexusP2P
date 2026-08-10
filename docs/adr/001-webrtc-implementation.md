# ADR-001：.NET 侧的 WebRTC 实现选 libdatachannel

- **日期**：2026-08-07
- **状态**：已采纳
- **对应任务**：Task 0.4（传输库 bake-off）
- **前置**：[SIPSorcery spike 结论](../spikes/2026-08-07-sipsorcery-datachannel.md)

## 背景

第一个候选 SIPSorcery 的 DataChannel 吞吐只有 **0.3 MiB/s**（本机回环），
比家庭上行带宽还慢一个数量级，20 GiB 要传 20 小时。
根因是其 SCTP 发送端的拥塞窗口不增长，而相关参数是编译期 `const`，绕不过去。

但那次 spike 也确认了：**坏的只是 SCTP 数据通道这一层，ICE 部分工作良好**。
而 ICE（NAT 打洞、候选收集、STUN/TURN 集成）恰恰是方向文档里明确
「绝不自研」的部分。所以正确的动作是换实现、不换架构。

判定门槛定为 **12 MiB/s**：典型家庭上行 50 Mbit/s ≈ 6 MiB/s，
协议栈至少要能达到它的两倍，才谈得上「瓶颈在网络而不在库」。

## 决定

采用 **libdatachannel**，通过 NuGet 包 `DataChannelDotnet 1.3.1` 使用。

## 实测结果

复用了 SIPSorcery spike 的**同一份浏览器页面与驱动脚本**，
只替换 .NET 侧实现 —— 否则数字不可比。

| | SIPSorcery 10.0.13 | libdatachannel (DataChannelDotnet 1.3.1) |
|---|---|---|
| **吞吐（.NET → Chrome）** | **0.3 MiB/s** | **75.8 MiB/s** |
| 1 GiB 耗时 | ~57 分钟（推算） | **13.5 秒** |
| 速率曲线 | 全程爬升，never 逃出慢启动 | **第一秒即满速，全程 70~81 平稳** |
| 托管堆峰值 | 46~152 MiB | **3.4 MiB** |
| 工作集峰值 | 125~248 MiB | 74.5 MiB |
| 数据完整性 | ✓ | ✓ |
| 背压 | 需轮询（无事件） | 需轮询（封装层未暴露事件） |
| 20 GiB 推算 | ~20 小时 | **~4.6 分钟**（回环） |

**253 倍**差距。托管堆只有 3.4 MiB 是因为发送队列在原生库里，不占托管堆 ——
这对「exe 后台常驻传 20 GB」这个场景是实打实的好处。

### 调参矩阵：76 MiB/s 是真实上限

跑了一组对照，专门排除「是我的测量循环在限速」这个疑点：

| 配置 | 分片 | 背压水位 | 吞吐 | 备注 |
|---|---|---|---|---|
| A 基线 | 64 KiB | 8 MiB | 76.2 MiB/s | |
| B 高水位 | 64 KiB | 32 MiB | 76.7 MiB/s | 几乎无变化 |
| C 大分片 | 256 KiB | 32 MiB | 78.8 MiB/s | +3% |
| D SCTP 调参 | 256 KiB | 32 MiB | **连接失败** | 见下 |

背压等待占了总时长的 ~78%，一度怀疑是 `Task.Delay(1)`（Windows 上实际睡 15.6ms）
在限速。**把水位从 8 MiB 抬到 32 MiB 后吞吐没变**，说明限速不在轮询循环，
76 MiB/s 是库与回环的真实上限。

### 别碰 SCTP 调参

`RtcTools.SetSctpSettings` 能改 `sendBufferSize`、`maxChunksOnQueue`、
`initialCongestionWindow`、`maxBurst`、`congestionControlModule` ——
正是 SIPSorcery 写死成 `const` 的那些旋钮。

但配置 D **让连接在传输中途失败**（`RTC_ICE_FAILED`）。原因是
`rtcSetSctpSettings` 接收一个完整结构体，只填一个字段时其余默认为 0，
而库把 0 当成「设为 0」而不是「保持默认」。

**结论：不动 SCTP 设置。** 默认值已给到门槛的 6 倍，调参上限只有 +3%
却带来真实的连接失败风险。这个旋钮留在原地。

## 为产品选定的用法

### 必须绕开封装层的一处

`IRtcDataChannel` 只暴露了 `Send` / `IsOpen` / `IsClosed` / `Label` 与事件，
**没有 `BufferedAmount`，也没有低水位事件** —— 而背压是 `IDataChannel`
抽象里的硬要求（SIPSorcery spike 已证明这不是理论风险）。

libdatachannel 的 C API 是有的，封装层只是没往上抬：

```
Rtc.rtcGetBufferedAmount(int id)
Rtc.rtcSetBufferedAmountLowThreshold(int id, int amount)
Rtc.rtcSetBufferedAmountLowCallback(int id, cb)
```

spike 里用反射取出私有的 `RtcDataChannel._channelId` 直接调原始绑定。
**这不是可交付的做法。**产品里两条路：

1. **直接用 `Bindings.Rtc`**，不用托管封装（绑定层是自动生成的，覆盖完整的 C API）
2. 给上游提 PR 把这三个成员抬到 `IRtcDataChannel` 上

倾向 **1**：我们已经有自己的 `IDataChannel` 抽象，中间再套一层别人的封装
只是多一层可能漏掉功能的转译。而且能顺便用上低水位**回调**，
不必像现在这样轮询。

### 原生依赖：约 10 MB，随 exe 分发

| 文件 | 体积 |
|---|---|
| `datachannel.dll` | 1.7 MB |
| `libcrypto-3-x64.dll` | 7.0 MB |
| `libssl-3-x64.dll` | 1.3 MB |

包内还带 win-x86 / linux-x64 / osx-x64，所以跨平台是现成的（虽然当前只要 Windows）。

对「单文件 exe」的影响：需要 `IncludeNativeLibrariesForSelfExtract`
或者接受多文件分发。这不是问题，只是 Task 4.1 打包时要知道。

## 风险与缓解

| 风险 | 评估 | 缓解 |
|---|---|---|
| `DataChannelDotnet` 下载量低（约 6 千），可能停止维护 | 中 | 它是 libdatachannel **稳定 C API** 上的一层薄绑定，且绑定代码是自动生成的。真被弃了可以自己维护那一层 —— 相比之下，libdatachannel 本体活跃且被广泛使用 |
| 引入原生依赖（OpenSSL） | 低 | 已确认预编译库开箱可用，无需 cmake。OpenSSL 3.x 需要留意安全更新 |
| **NAT 打洞与 TURN 未验证** | **中** | 本次是回环测试，没碰 ICE 打洞与中继。`RtcPeerConfiguration.IceServers` 支持 STUN/TURN，但**必须在 Task 3.3 实测** |
| **长时间稳定性未验证** | **中** | 只测了 13.5 秒。20 GiB 跨公网要跑 50 分钟以上，内存与句柄的长期行为要在 Task 7.1 验收时确认 |
| 局域网千兆下 76 MiB/s 只占 64% | 低 | 跨公网场景瓶颈是家庭上行（6 MiB/s），有 12 倍余量。局域网一对多是二期，届时可再评估 |

## 明确没有测的东西

诚实记录，免得把「回环达标」误当成「全都验证过了」：

- **NAT 打洞成功率**、STUN/TURN 集成 → Task 3.3
- **exe ↔ exe** 互通（本次只测 .NET → 浏览器）→ Task 3.4
- **长时间传输**（小时级）的内存与句柄行为 → Task 7.1
- 反方向（浏览器 → .NET）—— SIPSorcery 那边测过是 9.5 MiB/s，
  libdatachannel 没单独测，但既然正向已达 76，反向不构成风险

## 被放弃的候选

- **Pion（Go）sidecar**：没走到这一步。本机没有 Go 工具链，而且
  libdatachannel 已达标。多一个进程与进程间大数据搬运的复杂度也不值得
- **fork SIPSorcery 改 `SctpDataSender`**：改别人的拥塞控制是有风险的精细活，
  且与「用开源框架、不自研协议」的原则相悖。现在没有理由做
- **Microsoft.MixedReality.WebRTC**：包在（8 万下载，含预编译 libwebrtc），
  但 2022 年已归档。没测 —— libdatachannel 先达标了，
  引入一个停止维护的大依赖没有必要
- **从源码编译 libdatachannel**：本机没有 cmake/ninja。
  NuGet 的预编译包让这条路完全不必走
