# 开发计划：P2P 高速文件传输平台

> 依据：[`docs/ideas/p2p-file-transfer.md`](../docs/ideas/p2p-file-transfer.md)（方向已确认）
> 与 [`docs/spikes/2026-08-07-sipsorcery-datachannel.md`](../docs/spikes/2026-08-07-sipsorcery-datachannel.md)（传输库假设已失败）
>
> 生成日期：2026-08-07

## Overview

做一个只给自己和朋友用的 P2P 大文件直传工具。发送方选文件/文件夹，得到一串
`111-111-111` 的九位数字码，口头念给对方；对方输入码后打通隧道直传，
服务器只做信令与打洞失败时的中继，不存任何文件字节。支持断点续传，
exe 端支持跨会话续传。所有人退出后房间蒸发。

MVP 范围是**跨公网一对一**；swarm 与一对多推迟到二期。

## Architecture Decisions

### AD-1：传输协议先在内存管道上开发，与 WebRTC 解耦

上一个 spike 证明 SIPSorcery 的 DataChannel 吞吐不可用，替代库尚未选定。
如果让整个项目等这个决定，会白白浪费时间。

因此定义 `IDataChannel` 抽象（消息式、有序、带 `BufferedAmount` 背压 ——
刻意贴合 WebRTC DataChannel 的语义），先用内存双工实现把
**分片、校验、加密、续传、发送/接收状态机全部做完并测透**。

这个决定有三重收益：

1. **传输库选型不阻塞 90% 的工作**（可与阶段 1、2 并行）
2. **续传逻辑比接真网更好测** —— 内存管道可以精确注入断连、延迟、丢包、
   乱序，真实网络反而难以复现这些
3. **换库成本降到接近零** —— 如果选定的库日后也出问题，换掉的只是一个类

### AD-2：哈希用 SHA-256，不用 BLAKE3

方向文档里写的是「BLAKE3 或 SHA-256」，现在定为 SHA-256：

- **浏览器原生支持**：`crypto.subtle.digest('SHA-256', ...)` 开箱可用，
  BLAKE3 需要引入 WASM。两端都原生支持这一点压倒了 BLAKE3 的速度优势
- **少一个原生依赖**：WebRTC 替代库很可能已经要引入一个原生依赖了，不宜再加
- **性能够用**：现代 CPU 有 SHA 硬件指令，20 GB 约 10~20 秒，
  只在「`.meta` 丢失后全量重扫」这条冷路径上才需要跑全量

叶子块大小定为 **64 KiB**（不是 BEP-52 规定的 16 KiB）—— 我们只借用它的
Merkle 树结构，不与 BitTorrent 互操作，所以可以把哈希次数降到 1/4。
叶子大小作为参数写进清单，日后可调。

### AD-3：前端只写一遍，通过后端抽象适配两种宿主

同一份前端产物同时被信令服务器（网页模式）和 exe（WebView2 模式）加载。
但两种模式下 WebRTC 跑在不同的地方：

| 模式 | UI 位置 | 谁在做 WebRTC | UI 怎么通信 |
|---|---|---|---|
| 网页 | 远端服务器 | 浏览器自己 | 直接调 `RTCPeerConnection` |
| exe | localhost | .NET 后台 | 调本地 HTTP/WS 让 .NET 干活 |

所以前端定义一个 `TransferBackend` 接口（`BrowserBackend` / `AgentBackend`
两个实现），这是 `IDataChannel` 在前端的镜像。UI 组件对宿主一无所知。

exe 模式下，本地 HTTP 服务同时**代理信令 WebSocket** 到远端服务器，
这样前端永远是同源请求，不用处理 CORS。

### AD-4：信令服务器完全无状态

房间只是内存里一个短暂的转发通道，进程重启全丢也无所谓 ——
因为续传的锚点是 Merkle 根，不是房间号（每次重发生成新码）。
这让服务器可以随时重启、不需要数据库、不需要 Redis。

### AD-5：路径安全在接收端强制执行

接收方按发送方给的清单落盘，清单里的路径是**不可信输入**。
必须拒绝 `..`、绝对路径、盘符、Windows 保留名（`CON`、`NUL`、`COM1` …）、
以及解析后逃出目标目录的任何路径。这是本项目唯一的高危安全面。

### AD-6：不做浏览器判断，只做能力探测

决定：**不按浏览器名称限制或分流**。不写 `if (isChrome)`，
只探测能力（`'showSaveFilePicker' in window`、OPFS 是否可用），
按探测结果自动选择落盘策略，探测不到就退到内存 Blob。

这条决定的诚实后果：**Firefox 与 Safari 上接收大文件会受内存或 OPFS 配额限制**。
这是被接受的自然降级，不是专门设计的路径 —— 不会为它做专门的 UI 或测试矩阵，
但也绝不会因为浏览器型号就把用户挡在门外。

大文件本来就属于 exe（见方向文档的网页/exe 边界表），
所以这个限制与产品定位是吻合的。

### AD-7：自动重连 3 次，之后转手动；房间保留 60 秒宽限期

决定的重连行为：连接断开后**自动重试 3 次**（指数退避），
仍失败则停下来让用户手动重连。

这条决定暴露了原计划里的一个缺陷：AD-4 说「房间在两端都断开后立即释放」，
那么网络抖动导致双方信令 WebSocket 同时掉线时，房间会被释放，
自动重连必然失败。所以**房间释放需要 60 秒宽限期**。

这不破坏无状态性 —— 房间仍然只在内存里，仍然不落盘，进程重启仍然全丢。

重连的两种情况要分清：

| 情况 | 房间是否还在 | 处理 |
|---|---|---|
| 网络抖动，两个进程都活着 | 在（宽限期内） | **自动重连**：重连信令 → 同房间 → 新建 PeerConnection → 按位图续传 |
| 发送方进程死了 | 不在 | **手动**：用户重开程序、生成新码；接收方靠 Merkle 根续传 |

注意自动重连**不用 ICE restart 或 SDP 重协商** —— 直接丢掉旧的
PeerConnection 建一个全新的，简单得多，且续传锚点本来就是位图而非连接。

### AD-8：部署相关的值全部外置到配置，不编译进代码

决定：域名、端口、TURN 地址与密钥、分享链接的基址，
全部放配置文件，**部署前改，不重新编译**。

三个角色对「服务器在哪」的需求不同，不能用一套逻辑糊过去：

| 角色 | 怎么知道服务器地址 | 需要配置吗 |
|---|---|---|
| 网页 | 同源，直接用 `location.origin` | **不需要**，零配置 |
| exe | UI 来自 localhost，必须显式知道远端地址 | 需要 |
| 服务器 | 生成分享链接要用「对外公开的 origin」 | 需要 |

最后一条容易被忽略：服务器**绑定的地址不等于对外公开的 URL** ——
反向代理、NAT、端口映射都会让两者不同。所以生成分享链接必须用
显式配置的 `PublicOrigin`，而不是从请求里推断（那样容易被 Host 头污染）。

### AD-9：接收目录有默认值、可自选、且记住选择

决定：接收端有一个默认落盘目录，用户可以改，**改了之后记住，
下次打开还是这个目录**。

exe 侧直截了当：设置持久化到 `%APPDATA%/NexusP2P/settings.json`。

网页侧有个**浏览器安全模型决定的限制，绕不过去**：
支持 File System Access API 时，可以把目录句柄存进 IndexedDB 下次复用，
但浏览器可能要求**重新授权**（`queryPermission` 返回 `prompt`）。
不支持 FSA 时根本没有「目录」这个概念，只能走浏览器默认下载流程。

所以「记住目录」在网页端是**尽力而为**，不是保证。
UI 要如实反映当前状态，不能假装记住了。

## 项目结构

```
src/
  NexusP2P.Core/                    纯逻辑：Merkle、清单、文件码、加密
  NexusP2P.Transfer/                传输状态机、分片仓储、续传
  NexusP2P.Transport.Abstractions/  IDataChannel + 内存实现（含故障注入）
  NexusP2P.Transport.WebRtc/        真实实现（阶段 0 选型后填充）
  NexusP2P.Signaling/               ASP.NET Core 9 信令服务器
  NexusP2P.Agent/                   exe：托盘 + WebView2 + 本地 HTTP
  NexusP2P.Web/                     前端（TypeScript + Vite），两端共用
tests/
  NexusP2P.Core.Tests/
  NexusP2P.Transfer.Tests/
  NexusP2P.Integration.Tests/
spike/                           已有，测量脚手架可复用
docs/                            已有
tasks/                           本文档
```

## Definition of Done（项目级标准，每个任务都要过）

- `dotnet build` 零警告零错误
- `dotnet test` 全绿
- 前端改动：`npm run build` 与 `npm run test` 通过
- 新增的公开行为有对应测试；修 bug 先写能复现的失败测试
- 不留 `TODO` 而不记录到 `tasks/todo.md`
- 涉及不可信输入的改动，显式写出对恶意输入的处理

## 验证命令

```bash
dotnet build                                      # 构建
dotnet test                                       # 全部测试
dotnet test --filter FullyQualifiedName~Merkle    # 聚焦测试
cd src/NexusP2P.Web && npm run build && npm run test # 前端
```

---

# 任务分解

## 阶段 0：廉价实测与传输库选型（最高风险，先做）

> 这一阶段的任务**可与阶段 1、2 完全并行**。只有阶段 3 依赖 0.4 的结论。
> 0.1~0.3 都是几分钟到半小时的实测，但每一条都可能推翻某部分设计 ——
> 越早做越便宜。

### Task 0.1：实测 UDP 是否被 QoS 限速

**Description:** **443 已确认可用**（现有网站正常运行），
所以只剩 UDP 这一个未知项 —— 家宽的 UDP 有时被运营商 QoS 限速，
而 WebRTC 的媒体与数据通道全部跑在 UDP 上。若 UDP 被显著限速，
「打洞成功但速度反而不如中继」这种反直觉情况就会出现，
瓶颈显示逻辑（Task 5.4）必须能反映它。

**Acceptance criteria:**
- [ ] 记录 3478（UDP）、5349、49152~65535 抽样的可达性
- [ ] 实测 UDP 上下行速率，与同条件下的 TCP 速率对比，
      给出「UDP 是否被限速」的明确结论与倍数差异
- [ ] 结论写入 `docs/spikes/network-constraints.md`，
      并注明 443 已确认开放（无需再测）

**Verification:**
- [ ] 手工检查：从外网机器对家用服务器的公网 IP 做 UDP/TCP 对比测速

**Dependencies:** None
**Files likely touched:** `docs/spikes/network-constraints.md`
**Estimated scope:** XS

---

### Task 0.2：实测各浏览器落盘能力的实际天花板

**Description:** 按 AD-6，**不做浏览器限制**，所以这个 spike 的目的不再是
「决定排除谁」，而是**测出每种落盘策略的实际上限**，
作为运行时自适应选择的依据（以及在 UI 上如实告知用户的数字来源）。

三种策略按能力探测优先级排列：
`showSaveFilePicker` 流式落盘 → OPFS → 内存 Blob。

**Acceptance criteria:**
- [ ] 对每种策略测出实际可用上限与内存峰值曲线
      （建议梯度：1 / 2 / 5 GB，记录在哪一档失败）
- [ ] 在 Chrome、Edge、Firefox 各跑一遍，记录**每种策略在该浏览器上是否可用**
      （按能力探测，不按 UA 判断）
- [ ] 给出运行时选择策略的判定顺序，以及每种策略应向用户展示的上限提示
- [ ] 结论写入 `docs/spikes/browser-storage.md`

**Verification:**
- [ ] 手工检查：每个浏览器各跑一次，成功落盘的文件哈希与源一致
- [ ] 手工检查：确认没有任何一处代码依赖 UA 字符串

**Dependencies:** None
**Files likely touched:** `spike/BrowserStorage/index.html`, `docs/spikes/browser-storage.md`
**Estimated scope:** S

---

### Task 0.3：实测校园 Wi-Fi 的 AP isolation

**Description:** 若校园网开了客户端隔离，局域网直连失效、ICE 会退回中继，
二期 swarm 的价值直接归零。不阻塞 MVP，但结论影响二期是否值得做。

**Acceptance criteria:**
- [ ] 在目标校园网下用两台设备互相 ping / 建 TCP 连接，记录是否可达
- [ ] 结论追加到 `docs/spikes/network-constraints.md`

**Verification:**
- [ ] 手工检查：实地测试

**Dependencies:** None
**Files likely touched:** `docs/spikes/network-constraints.md`
**Estimated scope:** XS

---

### Task 0.4：传输库选型 bake-off

**Description:** 用 `spike/SipSorceryThroughput/` 已有的脚手架
（浏览器页面、序号校验、吞吐/内存/背压采集、Playwright 驱动全部可复用），
依次测候选库，判定门槛已写死在代码里：**12 MiB/s**。
按推荐度依次尝试，一旦达标即停止。

**Acceptance criteria:**
- [ ] libdatachannel + P/Invoke：跑通并记录吞吐、内存、背压行为
- [ ] 若未达标，测 Pion sidecar（本地管道通信）
- [ ] 若仍未达标，评估 fork SIPSorcery 的 `SctpDataSender`
      （放开 `MAX_BURST`、修 cwnd 增长条件）
- [ ] 选定方案写成 ADR：`docs/adr/001-webrtc-implementation.md`，
      含各候选的实测数字与放弃理由
- [ ] 确认所选方案的 ICE / TURN 能力完整（这是留用 WebRTC 的唯一理由）

**Verification:**
- [ ] 自动检查：spike 输出「吞吐达标：通过」
- [ ] 手工检查：与 Chrome 互通，1 GiB 传输序号连续、字节数一致

**Dependencies:** None
**Files likely touched:**
- `spike/LibDataChannelThroughput/` 或 `spike/PionThroughput/`
- `docs/adr/001-webrtc-implementation.md`

**Estimated scope:** L —— 这是全项目风险最高的单个任务

---

### Checkpoint 0
- [ ] 传输库已选定，实测吞吐 ≥ 12 MiB/s
- [ ] UDP 是否被 QoS 限速已有明确结论（443 已确认可用）
- [ ] 三种落盘策略的实际上限已测出，可作为运行时自适应的依据
- [ ] **与人复核**：若三个候选库全部不达标，需要重新讨论方向
      （退到备胎：网页只 P2P 发送不 P2P 收）

---

## 阶段 1：纯逻辑地基（不依赖任何网络）

### Task 1.1：解决方案骨架

**Description:** 建立 sln、各项目、项目引用、xunit 测试工程、
`.editorconfig`、`Directory.Build.props`（统一 `net9.0`、
`TreatWarningsAsErrors`、`Nullable enable`），以及一条能跑通的空测试。

**Acceptance criteria:**
- [ ] `dotnet build` 与 `dotnet test` 均成功
- [ ] 警告视为错误已开启，可空引用检查已开启
- [ ] 项目引用方向正确：`Core` 不依赖任何其他项目

**Verification:**
- [ ] 自动检查：`dotnet build && dotnet test`

**Dependencies:** None
**Files likely touched:** `NexusP2P.sln`, `Directory.Build.props`, `.editorconfig`, 各 `.csproj`
**Estimated scope:** S

---

### Task 1.2：Merkle 树与分片计算

**Description:** 实现 BEP-52 风格的 Merkle 树：64 KiB 叶子块（可配），
逐层 SHA-256 到根。提供「给定分片索引与数据，独立校验该分片」的 API ——
这是断点续传和二期 swarm 的地基。

**Acceptance criteria:**
- [ ] 输入流 → 叶子哈希列表 → 树 → 根哈希
- [ ] 单个分片可脱离整体独立校验（给出该分片的兄弟哈希路径）
- [ ] 边界正确：空文件、小于一个叶子、恰好整数个叶子、末尾不足一个叶子
- [ ] 相同输入恒定产出相同根（确定性）

**Verification:**
- [ ] 自动检查：`dotnet test --filter FullyQualifiedName~Merkle`
- [ ] 含固定测试向量（写死几个已知输入的根哈希，防未来重构改变语义）

**Dependencies:** 1.1
**Files likely touched:**
- `src/NexusP2P.Core/Hashing/MerkleTree.cs`
- `src/NexusP2P.Core/Hashing/PieceVerifier.cs`
- `tests/NexusP2P.Core.Tests/MerkleTreeTests.cs`

**Estimated scope:** M

---

### Task 1.3：传输清单（单文件与文件夹）

**Description:** 清单描述一次传输的全部内容：文件列表、每个文件的相对路径与
长度、Merkle 根、叶子与分片大小。单文件是只有一项的清单，
文件夹是多项 —— 让上层完全不区分两者。

**Acceptance criteria:**
- [ ] 序列化/反序列化往返无损
- [ ] 支持嵌套目录与空目录
- [ ] **路径安全**：拒绝 `..`、绝对路径、盘符、Windows 保留名、
      以及解析后逃出目标根目录的路径（见 AD-5）
- [ ] 清单本身有整体哈希，作为传输的身份标识

**Verification:**
- [ ] 自动检查：`dotnet test --filter FullyQualifiedName~Manifest`
- [ ] 恶意路径用例齐全：`../../etc/passwd`、`C:\Windows\x`、`a/../../b`、`NUL`

**Dependencies:** 1.2
**Files likely touched:**
- `src/NexusP2P.Core/Manifest/TransferManifest.cs`
- `src/NexusP2P.Core/Manifest/SafePath.cs`
- `tests/NexusP2P.Core.Tests/ManifestTests.cs`
- `tests/NexusP2P.Core.Tests/SafePathTests.cs`

**Estimated scope:** M

---

### Task 1.4：文件码生成与解析

**Description:** 九位十进制码，展示为 `111-111-111` 便于口头传达。
生成必须用 `RandomNumberGenerator` 而非 `Random`。
同时定义完整分享 URL 的格式，密钥放 URL fragment。

**Acceptance criteria:**
- [ ] 生成九位码，格式化为三组
- [ ] 解析容错：忽略连字符、空格、全角字符
- [ ] 拒绝长度不符或含非数字的输入，给出明确错误
- [ ] 分享 URL 格式：`{PublicOrigin}/r/111111111#<base64url 密钥>`，
      **基址从配置传入，不硬编码**（AD-8）
- [ ] 有测试断言密钥位于 fragment（保证不会发往服务器）

**Verification:**
- [ ] 自动检查：`dotnet test --filter FullyQualifiedName~TransferCode`
- [ ] 统计检查：生成 100 万个码，无明显偏斜

**Dependencies:** 1.1
**Files likely touched:**
- `src/NexusP2P.Core/Codes/TransferCode.cs`
- `tests/NexusP2P.Core.Tests/TransferCodeTests.cs`

**Estimated scope:** S

---

### Task 1.5：分片加密

**Description:** AES-GCM 加密每个分片。密钥材料来自文件码的 URL fragment，
经 HKDF 派生出内容密钥。**每个分片的 nonce 必须唯一** ——
由分片索引确定性派生，并在代码注释里写清不重用的论证。

**Acceptance criteria:**
- [ ] 加密/解密往返一致
- [ ] nonce 由分片索引派生，不同索引必不相同（有测试断言）
- [ ] 认证标签校验失败时抛出明确异常，不返回部分明文
- [ ] 密钥材料 → 内容密钥走 HKDF，不直接拿码当密钥

**Verification:**
- [ ] 自动检查：`dotnet test --filter FullyQualifiedName~Crypto`
- [ ] 篡改用例：改密文任意一字节，解密必须失败

**Dependencies:** 1.4
**Files likely touched:**
- `src/NexusP2P.Core/Crypto/PieceCipher.cs`
- `src/NexusP2P.Core/Crypto/KeyDerivation.cs`
- `tests/NexusP2P.Core.Tests/PieceCipherTests.cs`

**Estimated scope:** M

---

### Checkpoint 1
- [ ] `dotnet build` 零警告，`dotnet test` 全绿
- [ ] Merkle、清单、文件码、加密四块地基均有测试覆盖
- [ ] 路径安全与 nonce 唯一性有专门的对抗性测试

---

## 阶段 2：传输协议（内存管道上跑通，仍不碰真实网络）

> 这是全项目最关键的阶段。做完之后，**协议的正确性在没有网络的前提下已被证明**，
> 剩下的工作都是接线。

### Task 2.1：`IDataChannel` 抽象与内存实现

**Description:** 定义贴合 WebRTC DataChannel 语义的抽象：消息式（非流式）、
有序、有 `BufferedAmount` 背压、有 `MaxMessageSize`。
再实现一个内存双工版本，且**支持故障注入** ——
可配置延迟、丢包、乱序、指定字节数后强制断连。

**Acceptance criteria:**
- [ ] 抽象包含：发送、接收事件、`BufferedAmount`、`MaxMessageSize`、
      状态与关闭事件
- [ ] 内存实现的两端可互发，消息边界保持
- [ ] 故障注入可用：延迟、断连点、限速
- [ ] 背压语义与真实 DataChannel 一致（`BufferedAmount` 随发送增长、
      随对端消费下降）

**Verification:**
- [ ] 自动检查：`dotnet test --filter FullyQualifiedName~DataChannel`

**Dependencies:** 1.1
**Files likely touched:**
- `src/NexusP2P.Transport.Abstractions/IDataChannel.cs`
- `src/NexusP2P.Transport.Abstractions/InMemoryDataChannel.cs`
- `src/NexusP2P.Transport.Abstractions/FaultInjection.cs`
- `tests/NexusP2P.Transfer.Tests/InMemoryDataChannelTests.cs`

**Estimated scope:** M

---

### Task 2.2：协议消息与二进制帧格式

**Description:** 定义控制消息与数据消息。**用二进制帧而非 JSON** ——
20 GB 传输下 JSON 的编解码开销与体积都不可接受。
消息类型至少包括：`Manifest`、`Bitfield`、`Request`、`Piece`、
`Done`、`Error`。

**Acceptance criteria:**
- [ ] 每种消息序列化/反序列化往返无损
- [ ] 帧头含类型与长度，能从字节流中正确切分
- [ ] 收到未知消息类型时安全忽略（保留向前兼容）
- [ ] 拒绝声明长度超过 `MaxMessageSize` 的帧（防恶意超大帧）

**Verification:**
- [ ] 自动检查：`dotnet test --filter FullyQualifiedName~Protocol`
- [ ] 畸形输入用例：截断帧、长度字段撒谎、类型越界

**Dependencies:** 1.3
**Files likely touched:**
- `src/NexusP2P.Transfer/Protocol/Messages.cs`
- `src/NexusP2P.Transfer/Protocol/FrameCodec.cs`
- `tests/NexusP2P.Transfer.Tests/FrameCodecTests.cs`

**Estimated scope:** M

---

### Task 2.3：接收端分片仓储（`.part` / `.meta`）

**Description:** 把收到的分片写入 `<清单哈希>.part` 的正确偏移，
已完成分片位图存到 `<清单哈希>.meta`。**`.meta` 只是加速手段**：
丢失时退化为对 `.part` 全量重扫（用 Merkle 逐分片校验），删了不会坏。

**Acceptance criteria:**
- [ ] 分片可乱序写入正确偏移（稀疏文件）
- [ ] 位图持久化，进程重启后能恢复进度
- [ ] `.meta` 删除后能通过全量重扫重建位图，结果与原位图一致
- [ ] `.meta` 内容被损坏时，检测到并退化为重扫，而不是信任错误数据
- [ ] 全部分片完成后，重命名为最终文件名并做一次整体根校验

**Verification:**
- [ ] 自动检查：`dotnet test --filter FullyQualifiedName~PieceStore`
- [ ] 手工检查：传一半 → 删 `.meta` → 恢复 → 进度正确

**Dependencies:** 1.2, 1.3
**Files likely touched:**
- `src/NexusP2P.Transfer/Storage/PieceStore.cs`
- `src/NexusP2P.Transfer/Storage/PieceBitfield.cs`
- `tests/NexusP2P.Transfer.Tests/PieceStoreTests.cs`

**Estimated scope:** M

---

### Task 2.4：发送端状态机

**Description:** 发送方逻辑：发清单 → 等对端位图 → 只发对端缺的分片 →
按 `BufferedAmount` 做背压 → 收到完成通知后收尾。
背压必须**基于事件或低频轮询**，绝不忙等。

**Acceptance criteria:**
- [ ] 只发送对端位图里缺失的分片（这是断点续传的发送侧）
- [ ] `BufferedAmount` 超过高水位时暂停投递，回落后继续
- [ ] 对端中途断开时干净地停止，不抛未处理异常
- [ ] 暴露进度事件：已发字节、总字节、当前速率

**Verification:**
- [ ] 自动检查：`dotnet test --filter FullyQualifiedName~Sender`
- [ ] 用故障注入通道断连，断言不泄漏任务与文件句柄

**Dependencies:** 2.1, 2.2, 2.3
**Files likely touched:**
- `src/NexusP2P.Transfer/SendSession.cs`
- `tests/NexusP2P.Transfer.Tests/SendSessionTests.cs`

**Estimated scope:** M

---

### Task 2.5：接收端状态机

**Description:** 接收方逻辑：收清单 → 校验路径安全 → 查本地 `.part`
决定已有进度 → 回发位图 → 逐分片校验后落盘 → 全部完成后通知发送方。
**校验失败的分片必须丢弃并重新请求**，绝不落盘。

**Acceptance criteria:**
- [ ] 清单路径通过 `SafePath` 校验，任一路径非法则整体拒绝并报错
- [ ] 本地已有同清单哈希的 `.part` 时，位图反映已有进度
- [ ] 分片 Merkle 校验失败则丢弃并重新请求，不写入
- [ ] 全部完成后整体根校验通过，才重命名为最终文件
- [ ] 暴露进度事件：已收字节、总字节、当前速率

**Verification:**
- [ ] 自动检查：`dotnet test --filter FullyQualifiedName~Receive`
- [ ] 恶意用例：清单含 `../` 路径、分片数据被篡改

**Dependencies:** 2.1, 2.2, 2.3
**Files likely touched:**
- `src/NexusP2P.Transfer/ReceiveSession.cs`
- `tests/NexusP2P.Transfer.Tests/ReceiveSessionTests.cs`

**Estimated scope:** M

---

### Task 2.6：内存管道端到端测试

**Description:** 在内存管道上验证完整协议，包括最难的续传路径。
这一步通过之后，协议正确性就与网络无关了。

**Acceptance criteria:**
- [ ] 传 1 GiB 随机数据，接收端根哈希与源一致
- [ ] 传输至 40% 时强制断连 → 新建会话 → 从断点续传 → 最终一致
- [ ] 断连后删除 `.meta` → 仍能续传 → 最终一致
- [ ] 文件夹（含嵌套目录、空文件、单字节文件）整体传输一致
- [ ] 加密开启时全部以上用例仍通过

**Verification:**
- [ ] 自动检查：`dotnet test --filter FullyQualifiedName~EndToEnd`
- [ ] 记录内存峰值，确认不随文件大小线性增长

**Dependencies:** 2.4, 2.5, 1.5
**Files likely touched:** `tests/NexusP2P.Integration.Tests/InMemoryEndToEndTests.cs`
**Estimated scope:** M

---

### Task 2.7：重连策略（自动 3 次后转手动）

**Description:** 实现 AD-7 的重连策略。**策略本身与传输实现无关**，
所以放在这一阶段用故障注入通道测透 —— 真实网络下的重新协商是 Task 3.5。

**Acceptance criteria:**
- [ ] 连接断开后自动重试，最多 3 次，指数退避（如 1s / 2s / 4s）
- [ ] 3 次失败后停止并进入「等待手动重连」状态，暴露明确的状态与原因
- [ ] 每次重连成功后从位图续传，不重传已完成分片
- [ ] 重连期间的进度与状态对上层可见（供 UI 显示「正在重连 2/3」）
- [ ] 用户主动取消时立即停止，不再重试

**Verification:**
- [ ] 自动检查：`dotnet test --filter FullyQualifiedName~Reconnect`
- [ ] 用故障注入制造「断一次即恢复」「断三次后恢复」「断四次」三种场景，
      断言行为分别为：自动恢复、自动恢复、转手动

**Dependencies:** 2.4, 2.5
**Files likely touched:**
- `src/NexusP2P.Transfer/Reconnect/ReconnectPolicy.cs`
- `src/NexusP2P.Transfer/TransferSession.cs`
- `tests/NexusP2P.Transfer.Tests/ReconnectPolicyTests.cs`

**Estimated scope:** M

---

### Checkpoint 2 —— 最重要的检查点
- [ ] 1 GiB 传输、断点续传、`.meta` 丢失恢复、文件夹、加密全部通过
- [ ] 自动重连 3 次后转手动的行为符合 AD-7
- [ ] 内存占用不随文件大小线性增长
- [ ] **协议正确性已在零网络依赖下被证明**
- [ ] 与人复核后再接真实网络

---

## 阶段 3：真实网络

### Task 3.1：信令服务器

**Description:** ASP.NET Core 9，无状态，房间只存内存。
按文件码把两端配对，转发 SDP 与 ICE 候选。
必须有速率限制 —— 九位码可被枚举。

**Acceptance criteria:**
- [ ] WebSocket 端点：发送方建房得码，接收方输码入房
- [ ] 转发 offer / answer / candidate，不解析不存储内容
- [ ] **房间在两端都断开后保留 60 秒宽限期再释放**（见 AD-7）——
      这是自动重连能成功的前提；仍然只在内存里，进程重启全丢不影响正确性
- [ ] 宽限期内用同一个码重新入房能回到原房间
- [ ] 速率限制：同 IP 每分钟入房尝试上限（默认 20 次），超限返回 429
- [ ] 不存在的码返回明确错误，且**错误信息不区分「码不存在」与「码已满」**
      （避免成为枚举预言机）
- [ ] `PublicOrigin`、TURN 地址与密钥、宽限期时长全部来自配置文件（AD-8），
      且启动时校验配置完整性 —— 缺 `PublicOrigin` 时快速失败并说明原因，
      而不是生成一堆指向 localhost 的废链接

**Verification:**
- [ ] 自动检查：`dotnet test --filter FullyQualifiedName~Signaling`
- [ ] 手工检查：两个 WebSocket 客户端完成一次配对与转发

**Dependencies:** 1.4
**Files likely touched:**
- `src/NexusP2P.Signaling/Program.cs`
- `src/NexusP2P.Signaling/RoomRegistry.cs`
- `src/NexusP2P.Signaling/RateLimiter.cs`
- `tests/NexusP2P.Integration.Tests/SignalingTests.cs`

**Estimated scope:** M

---

### Task 3.2：WebRTC 传输实现

**Description:** 用阶段 0 选定的库实现 `IDataChannel`。
把 ICE 候选收集、DTLS、DataChannel 生命周期都封在这一个类后面，
让上层状态机对 WebRTC 一无所知。

**Acceptance criteria:**
- [ ] 实现 `IDataChannel` 全部语义，包括 `BufferedAmount` 背压
- [ ] 阶段 2 的全部端到端测试换成这个实现后依然通过
- [ ] ICE 状态与连接状态变化有事件与日志
- [ ] 吞吐达到阶段 0 实测水平（不因封装退化）

**Verification:**
- [ ] 自动检查：`dotnet test --filter FullyQualifiedName~EndToEnd`
      （同一套测试跑真实传输）
- [ ] 手工检查：与 Chrome 互通传 1 GiB

**Dependencies:** 0.4, 2.1, 2.6
**Files likely touched:**
- `src/NexusP2P.Transport.WebRtc/WebRtcDataChannel.cs`
- `src/NexusP2P.Transport.WebRtc/PeerConnectionFactory.cs`
- `tests/NexusP2P.Integration.Tests/WebRtcEndToEndTests.cs`

**Estimated scope:** L

---

### Task 3.3：coturn 中继与临时凭据

**Description:** 部署 coturn 并让服务器下发**临时 TURN 凭据**
（HMAC 时限凭据，而不是写死的账号密码）。
打洞失败时自动落中继，且要能明确知道当前走的是直连还是中继 ——
这是后面「瓶颈说明」的输入。

**Acceptance criteria:**
- [ ] coturn 配置就绪，3478 与 5349 可用，UDP 中继端口段开放
- [ ] 服务器按请求生成时限凭据（默认有效期 1 小时）
- [ ] 客户端能拿到凭据并在打洞失败时成功走中继
- [ ] 能查询当前连接用的候选对类型（host / srflx / relay）

**Verification:**
- [ ] 手工检查：用 `iceTransportPolicy: 'relay'` 强制走中继，传输成功
- [ ] 自动检查：凭据生成的 HMAC 正确性有单元测试

**Dependencies:** 3.1, 3.2, 0.1
**Files likely touched:**
- `src/NexusP2P.Signaling/Turn/TurnCredentialService.cs`
- `deploy/coturn/turnserver.conf`
- `tests/NexusP2P.Integration.Tests/TurnCredentialTests.cs`

**Estimated scope:** M

---

### Task 3.4：跨进程端到端（两个 exe，真实网络）

**Description:** 两个独立进程通过真实信令与 WebRTC 完成一次传输，
包括续传。这是第一次真正意义上的「能用」。

**Acceptance criteria:**
- [ ] 进程 A 选文件得码，进程 B 输码接收，文件一致
- [ ] 传输中杀掉 A，重启 A 用新码重发，B 从断点续传成功
- [ ] 强制走中继时同样成功
- [ ] 文件夹传输成功

**Verification:**
- [ ] 自动检查：`dotnet test --filter FullyQualifiedName~CrossProcess`
- [ ] 手工检查：跨两台真实机器（最好跨公网）跑一次

**Dependencies:** 3.1, 3.2, 3.3
**Files likely touched:** `tests/NexusP2P.Integration.Tests/CrossProcessTests.cs`
**Estimated scope:** M

---

### Task 3.5：真实网络下的自动重连

**Description:** 把 Task 2.7 的重连策略接到真实网络上。
按 AD-7，重连**不做 ICE restart 或 SDP 重协商** ——
直接丢掉旧 PeerConnection 建一个全新的，走同一个房间（宽限期内）。

**Acceptance criteria:**
- [ ] 信令 WebSocket 断开后能自动重连并回到原房间（宽限期内）
- [ ] PeerConnection 断开后新建连接并从位图续传，不重传已完成分片
- [ ] 拔网线 10 秒后插回，传输自动恢复，无需用户操作
- [ ] 拔网线超过宽限期 + 3 次重试后，进入「等待手动重连」并给出明确原因
- [ ] 重连过程中不泄漏 PeerConnection、任务或文件句柄
      （重复断连 10 次后句柄数与内存不持续增长）

**Verification:**
- [ ] 自动检查：`dotnet test --filter FullyQualifiedName~Reconnect`
- [ ] 手工检查：真实拔网线测试，观察自动恢复

**Dependencies:** 2.7, 3.1, 3.4
**Files likely touched:**
- `src/NexusP2P.Transport.WebRtc/ReconnectingChannel.cs`
- `src/NexusP2P.Signaling/RoomRegistry.cs`
- `tests/NexusP2P.Integration.Tests/ReconnectTests.cs`

**Estimated scope:** M

---

### Checkpoint 3
- [ ] 真实网络下一对一传输与续传均成功
- [ ] 拔网线后自动重连恢复；超限后转手动
- [ ] 中继兜底可用，且能判断当前走直连还是中继
- [ ] 吞吐达到阶段 0 的实测水平
- [ ] **与人复核**：此时核心价值已经可用，可以决定后续投入

---

## 阶段 4：exe 外壳

### Task 4.1：本地 HTTP 宿主与 WebView2

**Description:** exe 内嵌一个 localhost HTTP 服务，托管前端产物并暴露
本地 API（供 `AgentBackend` 调用），同时**代理信令 WebSocket** 到远端
服务器，让前端永远同源。用 WebView2 显示这个页面。

**Acceptance criteria:**
- [ ] 监听 localhost 随机可用端口，托管前端静态产物
- [ ] 本地 API：创建发送会话、加入接收会话、查询进度、取消
- [ ] 信令 WebSocket 代理到远端，前端无需处理 CORS
- [ ] 远端信令地址来自配置文件（AD-8），缺失时给出明确提示而非静默失败
- [ ] WebView2 运行时缺失时给出可操作的提示与下载引导

**Verification:**
- [ ] 手工检查：启动 exe，界面加载，能创建会话并显示码

**Dependencies:** 3.2
**Files likely touched:**
- `src/NexusP2P.Agent/LocalHost/LocalWebHost.cs`
- `src/NexusP2P.Agent/LocalHost/SignalingProxy.cs`
- `src/NexusP2P.Agent/MainWindow.xaml`

**Estimated scope:** M

---

### Task 4.2：托盘常驻与后台传输

**Description:** 关闭窗口不中断传输，最小化到托盘。单实例：
第二次启动时激活已有窗口而不是起新进程。传输完成弹通知。

**Acceptance criteria:**
- [ ] 关闭窗口后传输继续，托盘图标显示进度或状态
- [ ] 从托盘可恢复窗口、可查看进度、可退出（退出前确认有活动传输）
- [ ] 单实例：重复启动激活已有窗口
- [ ] 传输完成/失败有系统通知

**Verification:**
- [ ] 手工检查：传输中关窗，确认继续；重复启动确认单实例

**Dependencies:** 4.1
**Files likely touched:**
- `src/NexusP2P.Agent/Tray/TrayIcon.cs`
- `src/NexusP2P.Agent/SingleInstance.cs`

**Estimated scope:** M

---

### Task 4.3：文件与文件夹选择、拖放

**Description:** 支持选单文件、选文件夹、以及把文件/文件夹拖到窗口上。
选中后即时计算清单（大文件的哈希要在后台跑并显示进度，不能卡界面）。

**Acceptance criteria:**
- [ ] 三种入口都能产生正确清单
- [ ] 清单计算在后台线程，界面显示「正在计算校验和」进度
- [ ] 计算可取消
- [ ] 20 GB 文件的清单计算不阻塞界面

**Verification:**
- [ ] 手工检查：拖入一个大文件夹，界面保持响应

**Dependencies:** 4.1, 1.3
**Files likely touched:**
- `src/NexusP2P.Agent/LocalHost/ManifestBuilder.cs`
- `src/NexusP2P.Web/src/components/DropZone.ts`

**Estimated scope:** M

---

### Task 4.4：本地设置持久化与接收目录记忆

**Description:** 实现 AD-9 的 exe 侧：设置存到
`%APPDATA%/NexusP2P/settings.json`，接收目录有默认值、可自选、
**选了之后记住**。这也是后续所有用户偏好（开机自启、通知开关）的落点。

**Acceptance criteria:**
- [ ] 默认接收目录为 `%USERPROFILE%/Downloads/NexusP2P`，首次运行自动创建
- [ ] 用户可改目录，改动立即持久化，重启程序后仍是该目录
- [ ] 设置文件损坏或缺失时退回默认值并重建，**不崩溃、不阻止启动**
- [ ] 目标目录不可写（被删、无权限、盘符消失）时，在传输**开始前**检测并提示，
      而不是传到一半失败
- [ ] 落盘前检查可用空间是否足够容纳整个清单，不足则提前拒绝并给出所需空间

**Verification:**
- [ ] 自动检查：`dotnet test --filter FullyQualifiedName~Settings`
      （含损坏文件、缺字段、目录不存在三种恢复用例）
- [ ] 手工检查：改目录 → 重启 → 确认仍是新目录

**Dependencies:** 4.1
**Files likely touched:**
- `src/NexusP2P.Agent/Settings/AgentSettings.cs`
- `src/NexusP2P.Agent/Settings/SettingsStore.cs`
- `tests/NexusP2P.Integration.Tests/SettingsStoreTests.cs`

**Estimated scope:** M

---

## 阶段 5：网页端

### Task 5.1：前端骨架与后端抽象

**Description:** Vite + TypeScript。定义 `TransferBackend` 接口
与两个实现（`BrowserBackend` 走原生 WebRTC，`AgentBackend` 走本地 API），
UI 组件对宿主无感知（见 AD-3）。

**Acceptance criteria:**
- [ ] `npm run build` 产出静态文件，可被信令服务器与 exe 同时托管
- [ ] 运行时能判断当前宿主并选用对应 backend
- [ ] `npm run test` 有针对 backend 选择逻辑的单测
- [ ] 能力差异在 UI 上明确呈现（网页模式不显示「跨会话续传」等）
- [ ] 宿主与能力判断**全部基于特性探测**，不读 UA 字符串（AD-6）

**Verification:**
- [ ] 自动检查：`npm run build && npm run test`

**Dependencies:** 0.2
**Files likely touched:**
- `src/NexusP2P.Web/package.json`, `vite.config.ts`
- `src/NexusP2P.Web/src/backend/TransferBackend.ts`
- `src/NexusP2P.Web/src/backend/BrowserBackend.ts`
- `src/NexusP2P.Web/src/backend/AgentBackend.ts`

**Estimated scope:** M

---

### Task 5.2：网页接收

**Description:** 输码 → 连接 → 接收 → 落盘。按 AD-6，
**不按浏览器型号限制**，只按能力探测选落盘策略：
`showSaveFilePicker` → OPFS → 内存 Blob，探测结果决定本次能收多大。

**Acceptance criteria:**
- [ ] 能力探测按 Task 0.2 定的顺序选策略，**代码中不出现 UA 判断**
- [ ] 支持流式落盘时不在内存里堆整个文件
- [ ] 当前策略的大小上限在开始前就告知用户，超限时引导用 exe 并说明原因
- [ ] 分片 Merkle 校验在浏览器侧同样执行（Web Crypto 的 SHA-256）
- [ ] 任何策略都不可用时给出明确错误，而不是静默失败或假装成功
- [ ] **目录记忆（AD-9，尽力而为）**：支持 FSA 时把目录句柄存进 IndexedDB
      下次复用；`queryPermission` 返回 `prompt` 时**如实提示需要重新授权**，
      不假装已经记住。不支持 FSA 时不显示目录相关 UI

**Verification:**
- [ ] 手工检查：Chrome 接收 1 GB，哈希与源一致
- [ ] 手工检查：Chrome 选目录 → 关标签页 → 重开 → 确认复用（或正确提示重新授权）
- [ ] 手工检查：Firefox 上自动退到 OPFS 或 Blob，小文件仍能正常收

**Dependencies:** 5.1, 3.1, 0.2
**Files likely touched:**
- `src/NexusP2P.Web/src/transfer/BrowserReceiver.ts`
- `src/NexusP2P.Web/src/storage/FileSystemWriter.ts`

**Estimated scope:** M

---

### Task 5.3：网页发送

**Description:** 网页选文件 → 计算清单（Web Crypto + Worker，不能卡主线程）
→ 生成码 → 发送。spike 数据显示这个方向稳态 9.5 MiB/s，
已超过典型家庭上行，所以这条路是真能用的。

**Acceptance criteria:**
- [ ] 清单计算在 Web Worker 里跑，主线程保持响应
- [ ] 背压用 `bufferedamountlow` 事件，不轮询
- [ ] 大文件明确提示「建议用 exe」但不强制阻止
- [ ] 与 exe 接收端互通成功

**Verification:**
- [ ] 手工检查：网页发 500 MB 给 exe，哈希一致

**Dependencies:** 5.1, 3.4
**Files likely touched:**
- `src/NexusP2P.Web/src/transfer/BrowserSender.ts`
- `src/NexusP2P.Web/src/workers/hash.worker.ts`

**Estimated scope:** M

---

### Task 5.4：进度与瓶颈说明

**Description:** 文件夹传输显示**整体一个进度条**（不逐文件），
带实时速度，并**说明当前瓶颈**。这是刻意的产品决定：
用户看到 3 MB/s 时第一反应是「是不是坏了」，应该直接告诉他为什么。

**Acceptance criteria:**
- [ ] 整体进度条 + 实时速度 + 剩余时间估算
- [ ] 瓶颈判定至少覆盖：`走中继中`、`对方下行已满`、`本机上行已满`、
      `磁盘 IO 瓶颈`、`对方缓冲区背压`、`正在计算校验和`
- [ ] 判定依据来自实际指标（候选对类型、`BufferedAmount` 趋势、
      磁盘写入速率与网络速率的比较），不是猜测
- [ ] 判定逻辑有单测（给定指标组合 → 期望结论）

**Verification:**
- [ ] 自动检查：`npm run test` 覆盖瓶颈判定
- [ ] 手工检查：强制走中继时显示「走中继中」

**Dependencies:** 5.2, 5.3, 3.3
**Files likely touched:**
- `src/NexusP2P.Web/src/progress/BottleneckDetector.ts`
- `src/NexusP2P.Web/src/components/ProgressPanel.ts`
- `src/NexusP2P.Web/tests/BottleneckDetector.test.ts`

**Estimated scope:** M

---

### Checkpoint 5
- [ ] 网页收发均可用，与 exe 双向互通
- [ ] 浏览器能力差异在 UI 上诚实呈现
- [ ] 瓶颈说明基于真实指标

---

## 阶段 6：部署

### Task 6.1：证书与 HTTPS

**Description:** **443 已确认可用**，所以服务直接监听 443，
分享链接是干净的 `https://域名/r/111111111#密钥`，不用带 `:8443`。

证书仍走 Let's Encrypt 的 **DNS-01** 验证：80 被封所以 HTTP-01 不可用；
TLS-ALPN-01 虽然现在可行（它用 443），但需要验证期间抢占 443 端口、
与常驻服务冲突。DNS-01 不碰端口、不中断服务，更省事。

HTTPS 是硬需求 —— WebRTC 与 File System Access API 都只在 secure context 下工作。

**Acceptance criteria:**
- [ ] DNS-01 自动签发与续期跑通（`win-acme` 或 `acme.sh`）
- [ ] 服务在 443 提供有效 HTTPS，分享链接不含非标端口
- [ ] 动态 IP 场景下 DDNS 更新可用
- [ ] 部署步骤写成文档，可重复执行

**Verification:**
- [ ] 手工检查：外网浏览器访问无证书告警，WebRTC 可用
- [ ] 手工检查：模拟续期成功，且续期期间服务不中断

**Dependencies:** 3.1
**Files likely touched:**
- `deploy/certificates/README.md`
- `deploy/README.md`
- `src/NexusP2P.Signaling/appsettings.Production.json`

**Estimated scope:** M

---

### Task 6.2：一键部署与运行手册

**Description:** 把信令服务器与 coturn 的部署固化下来，
含开机自启、日志、以及一个能快速判断「服务是否健康」的检查清单。

**Acceptance criteria:**
- [ ] 信令服务器与 coturn 均配置为开机自启
- [ ] 有健康检查端点，能一眼看出服务状态
- [ ] 日志落盘并轮转，不会撑满磁盘
- [ ] **配置清单文档化**（AD-8）：`PublicOrigin`、TURN 地址与密钥、
      证书路径、宽限期，以及 exe 侧的远端信令地址 ——
      逐项说明含义、示例值、改错了会怎样
- [ ] 运行手册涵盖：重启、看日志、换证书、改域名、排查打洞失败

**Verification:**
- [ ] 手工检查：重启机器后服务自动恢复，能完成一次传输

**Dependencies:** 6.1, 3.3
**Files likely touched:** `deploy/README.md`, `deploy/systemd/` 或 Windows 服务配置
**Estimated scope:** S

---

## 阶段 7：验收

### Task 7.1：方向文档的验收标准

**Description:** 跑通方向文档里定义的验收标准，这是 MVP 完成的定义：
把一个 15 GB 文件夹从我的机器传到朋友的 exe，中途拔网线并关掉发送端程序，
重开后（新文件码）从断点继续，最终 Merkle 根校验一致。

**Acceptance criteria:**
- [ ] 15 GB 文件夹跨公网传输成功
- [ ] 中途拔网线 → 恢复网络 → 自动或手动重连后继续
- [ ] 关掉发送端程序 → 重开 → 新码 → 从断点续传
- [ ] 最终整体 Merkle 根一致
- [ ] 全程内存占用平稳，不随传输量增长

**Verification:**
- [ ] 手工检查：真实跨公网两台机器完整跑一次，记录耗时与速度曲线

**Dependencies:** 3.4, 4.2, 6.2
**Files likely touched:** `docs/acceptance/mvp-acceptance.md`
**Estimated scope:** M

---

### Task 7.2：给朋友用之前的收尾

**Description:** MVP 交给朋友使用前的必要打磨。

**Acceptance criteria:**
- [ ] exe 有版本号与「关于」信息
- [ ] 首次运行有一句话说明怎么用
- [ ] 常见失败有可操作的提示（打洞失败、码错误、磁盘空间不足、
      WebView2 缺失）
- [ ] 有一份给朋友看的一页说明（怎么装、怎么用、遇到问题怎么办）

**Verification:**
- [ ] 手工检查：找一个朋友在不被指导的情况下完成一次接收

**Dependencies:** 7.1
**Files likely touched:** `docs/user-guide.md`, `src/NexusP2P.Agent/About.xaml`
**Estimated scope:** S

---

### Checkpoint 7 —— MVP 完成
- [ ] 验收标准全部通过
- [ ] 朋友能在无指导下完成一次接收
- [ ] 与人复核：决定是否进入二期

---

## 二期（明确推迟，不属于 MVP）

按方向文档，以下内容在 MVP 完成后再评估：

- **swarm 与一对多分发** —— 主场景是一对一，收益为零。
  但分片格式与位图交换的数据结构已在阶段 2 设计好，
  换调度器（顺序拉 → 稀有块优先）是局部手术
- **局域网 QUIC 升级** —— 仅在双方同局域网时触发。
  依赖 Task 0.3 的 AP isolation 结论，若校园网隔离客户端则价值归零
- **永不做**：mDNS 局域网发现（ICE 的 host candidate 已免费解决）、
  账号体系、服务端存储文件、跨平台客户端、推广

---

## 并行化机会

| 可并行 | 说明 |
|---|---|
| 阶段 0 ‖ 阶段 1、2 | **最大的并行收益**。传输库选型是长尾任务，而阶段 1、2 完全不依赖它 |
| 1.2 ‖ 1.4 | Merkle 与文件码互不相关 |
| 2.4 ‖ 2.5 | 发送端与接收端可并行，但需先定好 2.2 的消息契约 |
| 5.2 ‖ 5.3 | 网页收发可并行，共用 5.1 的 backend 抽象 |
| 6.1 ‖ 阶段 4、5 | 部署与客户端开发互不阻塞 |

| 必须串行 | 原因 |
|---|---|
| 1.2 → 1.3 → 2.3 | 清单依赖 Merkle，仓储依赖清单 |
| 2.2 → 2.4 / 2.5 | 消息契约必须先定，否则两端对不上 |
| 0.4 → 3.2 | 没选定库无法实现真实传输 |
| 2.6 → 3.2 | 协议必须先在内存里证明正确，否则真网调试会同时面对两类 bug |

## Risks and Mitigations

| 风险 | 影响 | 缓解 |
|---|---|---|
| **三个 WebRTC 候选库全部达不到 12 MiB/s** | 高 | Task 0.4 早做且有明确门槛。真失败则退备胎：网页只 P2P 发送（已实测 9.5 MiB/s 可用）不 P2P 收，exe↔exe 另寻方案。Checkpoint 0 设为人工复核点 |
| **libdatachannel 的 P/Invoke 封装工作量超预期** | 中 | 先只封 DataChannel + ICE 必需的最小面，不追求完整绑定。若两天内封不出可用版本，转 Pion sidecar |
| **校园网 AP isolation 使二期归零** | 低 | Task 0.3 提前实测。属于二期风险，不影响 MVP |
| **家宽 UDP 被 QoS 限速，打洞成功但速度差** | 中 | Task 0.1 实测 UDP 与 TCP 速率差。若确认被限，**中继（可走 TCP/TLS）反而可能比直连快** —— 这会颠倒「直连优于中继」的默认假设，Task 5.4 的瓶颈显示必须能反映 |
| **自动重连掩盖真实故障** | 低 | 3 次重试会让「网络确实不通」这件事晚 7 秒才暴露。Task 2.7 要求重连状态对 UI 可见（「正在重连 2/3」），不静默 |
| **20 GB 传输中的内存或句柄泄漏** | 中 | 阶段 2 的端到端测试就记录内存峰值；Task 7.1 用真实 15 GB 验证 |
| **接收端路径穿越漏洞** | 高 | AD-5 明确为唯一高危面。Task 1.3 与 2.5 都有专门的对抗性测试用例 |
| **AES-GCM nonce 重用** | 高 | Task 1.5 要求 nonce 由分片索引确定性派生，并有测试断言唯一性 |
| **exe 未签名被 SmartScreen 拦截** | 低 | **已决定不买证书**。Task 7.2 的用户指南必须教朋友怎么点「更多信息 → 仍要运行」，否则第一次使用就会卡住 |
| **Firefox/Safari 上收大文件受配额限制** | 低 | AD-6 的自然后果，已接受。Task 5.2 要求在开始前就告知当前策略的上限，而不是传到一半失败 |
| **磁盘空间不足或目标目录不可写，传到一半才发现** | 中 | 20 GB 传 50 分钟后因磁盘满而失败是最糟的失败模式。Task 4.4 要求在**开始前**检查可写性与可用空间 |
| **`PublicOrigin` 配错，生成的分享链接对方打不开** | 中 | AD-8 要求启动时校验配置完整性并快速失败；Task 6.2 的配置清单要写明「改错了会怎样」 |

## 已决问题（2026-08-07 确认）

| 问题 | 决定 | 落到哪里 |
|---|---|---|
| 项目名 | **NexusP2P** | 全部命名空间与项目名 |
| exe 代码签名 | **不买证书**，接受 SmartScreen 警告 | Task 7.2 的用户指南要教怎么点「仍要运行」 |
| 浏览器支持 | **不做浏览器限制**，只做能力探测 | AD-6，Task 0.2 / 5.1 / 5.2 |
| 失败重试 | **自动重连 3 次，之后转手动** | AD-7，Task 2.7 / 3.5，房间加 60 秒宽限期 |
| 443 端口 | **已确认可用**（现有网站正常运行） | Task 0.1 缩减为只测 UDP；Task 6.1 直接用 443 |
| 分享链接域名 | **做成配置，部署前定** | AD-8，Task 1.4 / 3.1 / 6.2 |
| 接收目录 | **有默认值、可自选、记住选择** | AD-9，Task 4.4 / 5.2 |

## Open Questions

暂无阻塞项。以下是实现过程中会自然浮现、但不需要现在决定的：

- **默认分片大小**：1 MiB 还是 4 MiB？影响续传粒度与协议开销的权衡，
  Task 2.3 有了真实数据后再调，届时它只是一个常量
- **同时传输的会话数上限**：MVP 可以先只支持一个，
  但 UI 结构要预留列表形态，免得日后返工
