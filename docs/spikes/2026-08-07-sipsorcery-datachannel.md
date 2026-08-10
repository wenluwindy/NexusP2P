# Spike 结论：SIPSorcery DataChannel 吞吐（2026-08-07）

> **结论：假设不成立。** SIPSorcery 与浏览器互通完全正常、数据零错误、
> 内存可控，但 **.NET 发往浏览器的吞吐只有 0.3 MiB/s**，在本机回环上
> 比典型家庭上行带宽还慢一个数量级。按此速度传 20 GiB 需要约 20 小时。
>
> 这否掉了方向文档里「SIPSorcery 作为主传输通道」的前提，
> 但**不否掉 WebRTC 这个架构选择**（见「该换什么」）。

被验证的假设（来自 `docs/ideas/p2p-file-transfer.md`）：

> SIPSorcery 的 DataChannel 能与浏览器稳定互通，并承受数小时 20GB 传输

## 测量结果

代码在 `spike/SipSorceryThroughput/`，原始日志在 `spike/SipSorceryThroughput/results/`。

| 方向 | 平均吞吐 | 稳态吞吐 | 20 GiB 预计耗时 |
|---|---|---|---|
| .NET → 浏览器（**产品主流程**） | **0.3 MiB/s** | 0.3 MiB/s（不增长） | ~20 小时 |
| 浏览器 → .NET | 3.5 MiB/s | **9.5 MiB/s** | ~1.6 小时 |

参照：典型家庭上行 50 Mbit/s ≈ 6 MiB/s；千兆局域网 ≈ 118 MiB/s。
以上全部是**本机回环**测得，不含任何真实网络开销。

### 通过的项

- **数据完整性**：两个方向的字节数完全一致，分片序号全部连续，无丢失无错序
- **内存可控**：托管堆峰值 46~152 MiB，工作集峰值 125~248 MiB，无泄漏迹象
- **背压有效**：`bufferedAmount` 轮询能压住发送速率，峰值 8.1 MiB 对水位 8 MiB
- **互通性**：ICE、DTLS、SCTP 协商全部正常，Chrome 与 Edge 均可
- **无 headless 伪影**：headless Chromium 与真实有头 Chrome 结果一致（均 0.3 MiB/s）

### 唯一失败的项

吞吐。而且不是差一点，是差 40 倍。

## 根因定位

用反射读取 `SctpDataSender` 内部状态（`spike/SipSorceryThroughput/SctpProbe.cs`）：

```
正向（.NET 发送）稳态：
  cwnd 22.6 KiB | ssthresh 256 KiB | rwnd 4,548 KiB | 在途 18.3 KiB
```

三个事实：

1. **rwnd 4.5 MiB 从未成为约束** —— 接收窗口充裕，不是经典的 rwnd 瓶颈
2. **cwnd 卡在 22 KiB 且几乎不增长** —— 从初始 4.3 KiB 爬到 27 KiB 花了 5 分钟。
   而 ssthresh 是 256 KiB，意味着全程处于慢启动阶段，cwnd 本该每个 RTT 翻倍
3. **在途字节恒定 18.3 KiB**，在 50ms 和 1ms 两种节拍下**数值完全相同**

库内的硬编码常量（反射自 10.0.13）：

```
BURST_PERIOD_MILLISECONDS = 50
MAX_BURST                 = 4
DEFAULT_SCTP_MTU          = 1300
CONGESTION_WINDOW_FACTOR  = 4380
```

### 机制推断（未经源码确认，但与全部观测一致）

`MAX_BURST = 4` 意味着每个节拍最多投递 4 个 SCTP 分片 ≈ 5200 字节。
RFC 4960 的慢启动规则要求「**仅当 cwnd 被充分利用时**才增长 cwnd」。
每拍只发 5200 字节永远填不满 cwnd，于是增长条件几乎不触发 ——
**两个限制互相锁死**：发得慢导致窗口不长，窗口不长导致发得慢。

支持这个推断的三条证据：

- 把节拍从 50ms 反射改写为 5ms、1ms，吞吐只从 0.3 升到 0.4、0.5 MiB/s
  （**次线性**，说明节拍不是主因，但确实略微改善了 cwnd 的填充度 ——
  cwnd 终值也相应从 16 KiB 升到 27 KiB，方向一致）
- 反向测试中 Chrome 作发送方，前 45 秒同样在慢启动里爬，
  但**成功逃出**并稳定在 9.5 MiB/s。SIPSorcery 从未逃出
- 反向测试中 SIPSorcery 自己的 cwnd 一直停在初始值 4.3 KiB（它不发送），
  反证正向的瓶颈确实在发送端的拥塞控制

`MAX_BURST` 和 `BURST_PERIOD_MILLISECONDS` 都是 `const`，编译期内联，
**无法在运行时通过反射规避**。绕不过去，只能改库。

## 该换什么：换实现，不换架构

一个容易走错的结论是「WebRTC 不行，自己写 UDP 协议」。这是错的。

WebRTC 在这个项目里真正的价值**不是 SCTP，而是 ICE** ——
NAT 打洞、候选地址收集、STUN/TURN 集成、局域网 host candidate 自动直连，
这些是方向文档里明确列为「绝不自研」的部分，而 SIPSorcery 的 ICE 部分工作良好。

**坏掉的只有 SCTP 数据通道这一层。** 所以正确的动作是换掉 .NET 侧的
WebRTC 实现，保留整个架构。候选（按推荐度）：

1. **libdatachannel**（C++，MPL-2.0）+ P/Invoke ——
   内部用成熟的 usrsctp 做 SCTP、libjuice 做 ICE，吞吐口碑好。
   代价：引入原生依赖，需要写 P/Invoke 封装（或找现成绑定）
2. **Pion**（Go，MIT）作为旁路进程 ——
   exe 启动一个本地 sidecar，用命名管道/本地 socket 通信。
   Pion 极其成熟。代价：多一个可执行文件，进程间要传大量数据
3. **Fork SIPSorcery 改 `SctpDataSender`** ——
   改动面看似很小（放开 MAX_BURST、修 cwnd 增长条件），
   但改别人的拥塞控制是有风险的精细活，且与「用开源框架、不自研协议」的原则相悖。
   只在 1、2 都行不通时考虑

**下一个 spike 很便宜**：本次搭的测量脚手架可以直接复用 ——
浏览器端页面、序号校验、吞吐/内存/背压采集、Playwright 驱动全都不用改，
只需替换 .NET 侧的 WebRTC 实现。判定门槛也已经写死在代码里（12 MiB/s）。

## 对方向文档的影响

### 需要改的

- 「开源积木」里 **WebRTC 一项从 SIPSorcery 改为待定**，
  由下一个 spike 决出
- 「Key Assumptions」里第一条标记为**已验证且失败**

### 意外保住的：网页发、exe 收这条路今天就能用

浏览器 → .NET 稳态 9.5 MiB/s（76 Mbit/s），**已经超过典型家庭上行 6 MiB/s**。
也就是说即使完全不换库，「朋友用网页发给我、我用 exe 收」这个方向
在真实网络下瓶颈仍在带宽而非协议栈。

这给备胎方案提供了一个比原先设想更好的形态：不是「网页只能走中继」，
而是**网页可以 P2P 发送，只是不能 P2P 高速接收**。
考虑到大文件本来就该由 exe 承担（见方向文档里网页/exe 的边界表），
这个不对称限制和产品定位居然是吻合的。

### 不需要改的

架构、文件码设计、Merkle 分片层、续传机制、部署形态、
以及「打洞和 swarm 场景互斥」这个结论 —— 全部不受影响。

## 复现方式

```bash
cd spike/SipSorceryThroughput

# 正向：.NET 发 -> 浏览器收（复现 0.3 MiB/s）
dotnet run -c Release -- --size-mb 24 --port 5080
python drive_receiver.py --port 5080          # 另一个终端

# 反向：浏览器发 -> .NET 收（复现 9.5 MiB/s 稳态）
dotnet run -c Release -- --size-mb 256 --port 5096 --reverse
python drive_receiver.py --port 5096

# 节拍周期对照实验（50ms / 5ms / 1ms）
bash run_matrix.sh

# 反射查看库内部 API 与常量
dotnet run -c Release -- --api SIPSorcery.Net.SctpDataSender
```

可选参数：`--size-mb`、`--chunk-kb`、`--high-water-mb`、`--burst-ms`、
`--reverse`、`--verbose`；驱动脚本支持 `--headed`、`--channel chrome`。
