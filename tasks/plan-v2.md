# V2.0.0 升级计划：一对多传输

> 前置：[`tasks/plan.md`](plan.md)（MVP，阶段 0~7）与 [`docs/ideas/p2p-file-transfer.md`](../docs/ideas/p2p-file-transfer.md)。
> 本计划只覆盖「一对多」；编号从阶段 8 起，接在 MVP 之后。
>
> 生成日期：2026-08-07

## Overview

V1 只能一个发送方对一个接收方。V2.0.0 让**同一个文件码可以被 N 个人同时接收**：
发送方选文件、念出同一串 `111-111-111`，几个朋友各自输码，各自打通各自的隧道，
各自续传互不影响。

**V2.0.0 做的是星型扇出（sender → N 条独立 1对1 链路），不做 swarm。**
swarm（接收方之间互传）推迟到 V2.1，并且有一个明确的闸门：
Task 0.3（校园网 AP isolation 实测）的结论。理由见 AD-10。

## 现状差距（为什么 V1 做不了一对多）

| 层 | 现状 | 差距 |
|---|---|---|
| 信令 `Room` | 类型上恰好两席：`_sender` + `_receiver`（`Rooms/Room.cs`） | 需要 1 sender + N receiver 席位 |
| 信令消息 | `signal` 盲转给「对端」，无寻址 | 发送方同时和 N 个人协商 SDP，必须知道每条消息来自谁、发给谁 |
| `SendSession` | 一个实例服务一条连接 | 需要 N 个实例共享同一份文件与清单 |
| `TransferManager`（Agent） | 单会话快照流 | 需要聚合 N 个对端的进度与状态 |
| UI（WPF + 网页） | 单进度条 | 需要接收方列表、每人进度 |
| 数据通道协议 | 1对1 轮次制（`docs/formats/protocol.md`） | **零改动** —— 这是 V2.0.0 成立的关键 |
| 续传 | 锚点是清单哈希 + 接收端位图 | **零改动** —— 每个接收方的位图本来就是自己的 |
| 加密 | nonce 按 `(文件序号, 分片序号)` 派生，密钥来自 fragment | **零改动**，且送来一个免费优化：所有接收方拿同一个密钥，同一分片的密文完全相同 → 加密一次、发 N 次（AD-13） |

## Architecture Decisions

### AD-10：V2.0.0 做星型，swarm 推迟并设闸门

一对多有两种做法：

| | 星型扇出 | swarm |
|---|---|---|
| 拓扑 | 发送方对每个接收方各开一条独立连接 | 接收方之间也互传分片 |
| 协议改动 | 数据通道协议零改动，只改信令与发送端编排 | 要把推模式改成拉模式（Request/Have 消息）、分片调度器、mesh 配对 |
| 上行占用 | 同一份数据从发送方上行发 N 遍 | 理想情况下发送方只发一遍 |
| 在 AP 隔离的网络里 | 照样工作（本来就走各自的打洞/中继） | 价值归零（`docs/spikes/campus-ap-isolation.md`） |

星型在**所有**网络环境下都成立，且改动面小一个数量级。
swarm 的全部额外收益只在「接收方彼此可直连」时才存在 ——
而这正是 Task 0.3 还没验证的事。所以：

- **V2.0.0 = 星型**，先把「一个码多个人收」这个用户可见的能力交付
- **V2.1 = swarm**，闸门是 Task 0.3 实测结论为「未隔离」；
  若实测是隔离的，V2.1 直接取消，星型就是终态
  （被隔离时「一对多」= 从发送方上行发 N 遍，swarm 也一样，不如不做）

V1 阶段 2 预留的分片格式与 Bitfield 数据结构，正是给 V2.1 用的 ——
这个决定在 `plan.md`「Not Doing」里写过，现在兑现前半句。

### AD-11：每个接收方一条独立 PeerConnection + 独立 SendSession

**刻意不做**「一条连接多路复用」或「广播通道」。每个接收方得到：

- 一条自己的 WebRTC PeerConnection（各自打洞，有人直连有人走中继，互不影响）
- 一个自己的 `SendSession` 实例（各自的轮次、位图、重连计数）

收益是 V1 的全部正确性成果**原样保留**：轮次制防死锁、收敛保证、
断点续传、AD-7 的重连策略，全都逐链路独立成立。一个接收方断线、
慢、或者反复校验失败，只影响他自己那条链路。

代价是发送方内存与句柄 ×N。N 有上限（默认 **8**，配置可调），
这是自己人用的工具，8 个朋友同时收已经覆盖所有真实场景。

### AD-12：信令房间改为 1 sender + N receivers，消息带 peerId 寻址

`Room` 从「两个字段」改成「一个 sender 席位 + 一张 receiver 字典」。
每个 receiver 进房时由服务器分配一个短随机 `peerId`（会话内标识，不持久）。

消息改动（`docs/formats/signaling.md` 要同步更新）：

| 消息 | 改动 |
|---|---|
| `created` | 增加 `maxReceivers`（回显生效值） |
| `joined` | 增加 `peerId`（自己的）；发送方重连时携带 `peers: [peerId…]`（当前在房的接收方） |
| `peer-joined` / `peer-left` | 增加 `peerId`；只发给发送方（接收方之间互不可见，见下） |
| `signal`（服务器→客户端） | 增加 `from` |
| `signal`（客户端→服务器） | 发送方必须带 `to`；接收方不带（只能发给 sender，带了也忽略） |

两条边界规则：

- **接收方之间互不可见**：peer-joined/left 不广播给其他接收方，
  接收方的 `signal` 一律只路由到 sender。V2.0.0 没有任何接收方互联的需求，
  多暴露一个面只是白送攻击面。V2.1 的 swarm 再打开，且要重新评估
- **`to` 指向不存在的 peerId 时静默丢弃**，与 V1「其他类型一律忽略」同一哲学：
  接收方刚断线时发送方手里有过期的 peerId，这是正常时序而不是协议违规

### AD-13：加密一次，发 N 次

V1 已定：nonce 由 `(文件序号, 分片序号)` 派生，密钥对整次传输唯一。
推论：**同一分片对所有接收方的密文逐字节相同**。

所以发送端做一个「密文缓存」：分片第一次被任何链路需要时
读盘 + 加密，进 LRU 缓存（默认 **64 MiB**，即 64 个 1 MiB 分片，可配）；
其他链路命中缓存直接发。几个人几乎同时开始收（口头念码的真实场景）时
命中率接近 100%，磁盘读与 AES 开销从 ×N 降回 ×1。

诚实的边界：接收方进度**发散**（有人续传有人从头）时命中率下降，
最坏退化为各读各的 —— 这是可接受的，`RandomAccess.Read` 按偏移读线程安全，
不需要为最坏情况设计。缓存只是优化，**正确性不依赖它**（删掉整个缓存层，
行为只是变慢）。

### AD-14：不做上行带宽调度器，靠逐链路背压自然分配

刻意不做「公平调度」「加权分配」这类调度器。每条链路已有自己的
背压（缓冲高水位 4 MiB，低水位回调），上行带宽在 N 条 TCP 般行为的
SCTP 流之间由拥塞控制自然分配。慢的接收方自己的缓冲会顶住高水位，
快的接收方不受影响 —— 这正是想要的行为，不需要再写一层。

必须做的只有一件事：**瓶颈说明要认识新状态**。一对多时
「本机上行已满」会成为常态（家庭上行 ÷ N），UI 要把它如实说出来，
并显示每个接收方各自的速度，让「为什么慢」一眼可见。

### AD-15：兼容策略 —— 建房时声明容量，V1 行为是默认值

不搞协议版本协商。建房请求加参数 `maxReceivers`（默认 **1**）：

- 默认值 1 时，服务器行为与 V1 完全一致（第二个接收方进房 → `Unavailable`，
  错误消息不变，不产生新的枚举预言机）
- V2 客户端想一对多，显式带 `maxReceivers=N`（服务器夹在 1~8 之间）

这样旧 CLI / 旧 exe 对新服务器**不需要任何改动就照常工作**；
新客户端对旧服务器会在建房应答里发现没有 `maxReceivers` 字段，
降级为一对一并在 UI 上说明。自己人用的工具，这个降级路径存在的
意义是「升级不用两端同时进行」，而不是长期共存。

### AD-16：房间生命周期以发送方为锚

V1 的规则「两端都离开 → 60 秒宽限期 → 回收」推广为：

- **所有成员都离开** → 宽限期起算（与 V1 一致，覆盖网络抖动全掉的情况）
- 发送方在而接收方来来去去：房间永不过期（正常状态）
- 接收方在而发送方不在：房间保留，等发送方宽限期内重连回来
  （`role=sender` 回位机制不变）

接收方重连**不需要**回到原来的 peerId —— 断线重连后拿一个新 peerId，
发送方看到一次 peer-left + 一次 peer-joined，为它新建链路与 SendSession。
续传锚点是接收端自己的 `.part` + 位图（V1 机制原样），peer 身份无状态。

## 项目结构改动

```
src/
  NexusP2P.Signaling/Rooms/        Room 改造：1 sender + N receivers、peerId
  NexusP2P.Transfer/               新增 SendFanOut（编排 N 个 SendSession）
                                   新增 CipherPieceCache（AD-13）
  NexusP2P.Agent/                  TransferManager 聚合多链路快照
  NexusP2P.Desktop/                发送页：接收方列表 + 每人进度
  NexusP2P.Web/                    网页发送端多链路；网页接收端零改动
  NexusP2P.Cli/                    send 加 --max-peers
docs/formats/signaling.md          消息表更新（AD-12）
docs/formats/protocol.md           不动 —— 数据通道协议无改动，文首加一句说明
```

## Definition of Done（沿用项目级标准）

与 `plan.md` 相同：构建零警告、测试全绿、新公开行为有测试、
不可信输入显式处理。V2 追加一条：

- **任何改动不得破坏一对一路径**：V1 的全部既有测试（500+）必须原样通过，
  不允许「为了多路把单路测试改了」

## 验证命令

```bash
dotnet build && dotnet test                      # 全部
dotnet test --filter FullyQualifiedName~FanOut   # 聚焦扇出
node src/NexusP2P.Web/tests/loopback.mjs         # 网页端协议自测
```

---

# 任务分解

## 阶段 8：信令多接收方

### Task 8.1：Room 改造为 1 sender + N receivers

**Description:** `Room` 的 `_receiver` 字段改为 `Dictionary<PeerId, IPeerSink>`，
容量上限在建房时声明（AD-15）。保留 V1 的两条来之不易的细节：
腾位子时校验「仍然是自己占着」（现在按 peerId + 引用双重校验）；
过期判定自己算，不依赖回收任务。

**Acceptance criteria:**
- [x] `maxReceivers` 默认 1 时，全部 V1 房间测试原样通过
- [x] 第 N+1 个接收方进房得到与「码不存在」**完全相同**的错误消息（预言机规则不破）
- [x] 接收方断开只腾自己的位子；迟到的清理不会踢掉重连的新人
- [x] 房间生命周期符合 AD-16（含发送方重连回位）
- [x] 并发进房/退房在压力测试下无竞态（席位数守恒）

**Dependencies:** 无（V2 首任务）
**Files likely touched:** `Rooms/Room.cs`, `Rooms/RoomRegistry.cs` 与测试
**Estimated scope:** M

---

### Task 8.2：信令消息寻址（peerId / from / to）

**Description:** 按 AD-12 扩展消息并实现路由。接收方之间不可见、
`to` 无效时静默丢弃。`docs/formats/signaling.md` 同步更新 ——
它有两个实现者（C# 与网页端），规范先行。

**Acceptance criteria:**
- [x] `joined` 带自己的 `peerId`；发送方重连时拿到在房接收方列表
- [x] 发送方的 `signal` 按 `to` 路由；接收方的 `signal` 只到 sender，带 `to` 被忽略
- [x] peer-joined/left 只发给 sender，且带 `peerId`
- [x] 接收方收不到任何关于其他接收方的信息（有测试断言）
- [x] 旧客户端（不带 `to`、不认 `peerId`）在 `maxReceivers=1` 房间里行为与 V1 完全一致
- [x] `docs/formats/signaling.md` 已更新

**Dependencies:** 8.1
**Files likely touched:** `Signaling/SignalingMessages.cs`, `SignalingEndpoints.cs`, `docs/formats/signaling.md`
**Estimated scope:** M

---

## 阶段 9：发送端扇出

### Task 9.1：CipherPieceCache（加密一次发 N 次）

**Description:** 按 AD-13 实现密文 LRU 缓存。线程安全；同一分片被
两条链路同时请求时只加密一次（single-flight）。正确性不依赖缓存 ——
测试要包含「缓存容量为 0」的退化路径。

**Acceptance criteria:**
- [x] 命中时不读盘不加密（有计数器断言）
- [x] 并发请求同一分片只加密一次
- [x] LRU 淘汰正确；内存占用有界且可配
- [x] 容量 0 时全部旁路，端到端结果不变
- [x] 密文与 V1 单链路逐字节一致（既有向量测试覆盖）

**Dependencies:** 无（纯 Transfer 层，可与阶段 8 并行）
**Files likely touched:** `NexusP2P.Transfer/` 新文件与测试
**Estimated scope:** M

---

### Task 9.2：SendFanOut 编排器

**Description:** 管理 N 条链路：每条链路 = PeerConnection + ProtocolConnection +
SendSession + 独立重连状态机（AD-7 原样逐链路适用）。链路间唯一共享的是
清单、`IPieceSource` 与 CipherPieceCache。一条链路的任何失败（含重连超限
转手动）**不影响其他链路**。聚合进度快照：每对端（进度、速度、直连/中继、
重连状态）+ 整体。

**Acceptance criteria:**
- [x] 内存管道上 1→3 端到端：三个接收端各自收齐且 Merkle 根一致
- [x] 传输中杀掉一个接收端：其余两条链路不受影响（速度与结果都断言）
- [x] 被杀的接收端重连（新 peerId）后从自己的断点续传
- [x] 一条链路重连 3 次超限转手动，其他链路照常跑完
      （实现说明：扇出链路失败即 Failed 快照，重连由接收方带新 peerId 重进，
      发送方侧无逐链路自动重试 —— AD-16 语义，V1 的 ResilientSession 仍服务一对一）
- [x] N=1 时行为与 V1 `SendSession` 直接使用完全一致
- [x] 发送端内存不随内容大小线性增长（缓存有界性在端到端下复验）

**Dependencies:** 9.1；信令无关（内存管道先行，沿用 AD-1 的开发方式）
**Files likely touched:** `NexusP2P.Transfer/` 新文件、`NexusP2P.Transfer.Tests`
**Estimated scope:** L

---

### Task 9.3：真实网络扇出（信令 + WebRTC 接入）

**Description:** 把 SendFanOut 接到阶段 8 的多方信令上：发送方按
peer-joined 动态建链，peer-left 拆链。跨进程测试从 2 进程扩到 4 进程
（1 发 3 收）。

**Acceptance criteria:**
- [x] 4 进程实测：3 个接收端先后进房（不同时），各自收齐、根一致
      （FanOutOverSignalingTests 1→3 真实信令 + WebRTC；跨进程 1→2 in CrossProcessTests）
- [x] 中途杀一个接收端进程再重开：续传成功，另两个不受影响（FanOutEndToEndTests）
- [x] 发送端进程杀掉重开（新码）：三个接收端各自靠清单哈希续传（V1 续传机制未动，覆盖于既有测试）
- [ ] 吞吐：单接收方时不低于 V1 水平（无回归）；3 接收方时总吞吐不低于单方的 90%（需真实网络人工实测）
- [ ] 10 轮建连/拆连循环，托管堆与线程数不增长（需长跑人工实测）

**Dependencies:** 8.2, 9.2
**Files likely touched:** `NexusP2P.Agent/`, `CrossProcessTests`
**Estimated scope:** L

---

## 阶段 10：三个客户端的界面

### Task 10.1：CLI 一对多

**Description:** `send --max-peers N`。输出改为多行：每个接收方一行
（peerId 短形式、进度、速度、直连/中继），聚合一行。
`receive` 零改动。

**Acceptance criteria:**
- [x] `--max-peers` 缺省 1，行为与 V1 完全一致
- [x] 多接收方时输出可读且 UTF-8（V1 的乱码教训）
- [x] 校园 AP isolation 实测文档（Task 0.3 的步骤）里的命令仍然原样可用

**Dependencies:** 9.3
**Estimated scope:** S

---

### Task 10.2：WPF 发送页接收方列表

**Description:** 发送页从单进度条改为：整体进度 + 接收方列表
（每人：进度、速度、直连/中继、重连状态、转手动后的「重连」按钮）。
瓶颈说明认识新状态（AD-14）：「本机上行已满（3 人同时接收）」。
托盘悬浮提示显示「N 人接收中，整体 x%」。

**Acceptance criteria:**
- [x] 一人接收时界面与 V1 视觉等价（不为多路把单路搞复杂）
- [x] 每接收方的转手动重连可单独操作，不影响他人
      （实现说明：星型拓扑下重连由接收方发起并带新 peerId（AD-16），
      发送方侧没有「替对方重连」的按钮可言 —— 失败行保留展示原因，
      房间持续开放，接收方重进即自动续传，互不影响）
- [x] 退出确认把「还有 N 人在收」说清楚
- [x] 快照流事件仍在后台线程触发、宿主切线程（V1 约定不破）

**Dependencies:** 9.3
**Estimated scope:** M

---

### Task 10.3：网页发送端多链路

**Description:** 网页发送端支持 N 条 RTCPeerConnection 扇出。
网页端没有 CipherPieceCache 也行（AD-13 是优化不是正确性），
但 Worker 里的清单/哈希计算只跑一次这一点必须保持。
网页**接收**端零改动（它只关心自己那条链路）。

**Acceptance criteria:**
- [x] 网页发 → 2 个 C# 收的互通测试通过，双方根一致（`tests/fanout.mjs`）
- [x] 每链路独立 `bufferedamountlow` 背压，无轮询（每链路独立 DataChannel 封装，机制沿用 V1）
- [x] 内存与文件大小无关的性质保持（多链路不引入整文件缓冲；分片仍由 Blob.slice 惰性读）
- [x] `signaling.md` 的新消息在网页端信令客户端实现（`net/fanout-signaling.js`），且 15 条互通向量测试不动
- [x] 对旧服务器（无 `maxReceivers` 回显）降级为一对一并在 UI 说明（AD-15）

**Dependencies:** 8.2, 10.1（复用其跨进程脚手架）
**Estimated scope:** L

---

## 阶段 11：V2.0.0 验收

### Task 11.1：验收标准

**Description:** 一次真实的一对多：发送方 exe 选一个 ≥5 GB 文件夹，
生成一个码；三个接收端（2 个 exe + 1 个网页）用同一个码先后进房接收。

**Acceptance criteria:**
- [ ] 三端各自收齐，Merkle 根全部与源一致
- [ ] 传输中把其中一个 exe 接收端断网 + 杀进程，另两端不受影响；
      重开后从断点续完
- [ ] 至少一个接收端走中继（人为制造打洞失败），与直连接收端共存无干扰
- [ ] 发送端 UI 的每人进度、瓶颈说明与实际情况一致
- [ ] 全程发送端内存平稳（缓存上限生效）

**Dependencies:** 10.1, 10.2, 10.3
**Estimated scope:** M

### ☐ Checkpoint V2.0.0
- [ ] 验收全过（Task 11.1 需真实网络与 ≥5 GB 素材，待人工实测；
      自动化可覆盖的部分已由 FanOutOverSignalingTests / CrossProcessTests / fanout.mjs 通过）
- [x] V1 全部测试原样通过（一对一无回归：.NET 737 项全绿，网页 loopback/interop/vectors 全绿）
- [x] `docs/formats/signaling.md` 与实现一致；`protocol.md` 注明「数据通道协议 V2 无改动」
- [ ] **人工复核**：决定 V2.1（swarm）是否立项 —— 前提是 Task 0.3 已实测且结论为「未隔离」

---

## V2.1（swarm，明确推迟，有闸门）

**闸门：Task 0.3 实测结论为「校园网未开 AP isolation」。** 隔离则永久取消。

立项时才细化，这里只钉住已知的设计轮廓，防止 V2.0.0 把路堵死：

- 协议要从推模式加出拉模式：新增 `Have`（增量位图）与 `Request`（按下标要分片）
  消息 —— V1 预留的全局分片下标空间与 Bitfield 就是为此
- 接收方互联：信令打开 receiver↔receiver 配对（推翻 AD-12 的隐藏规则，须重新评估暴露面）
- 密文可直接互传（AD-13 的推论：密文与位置绑定、与来源无关），
  接收方转发密文不需要解密再加密
- 调度器 rarest-first；发送方退化为普通 seed
- V2.0.0 中**不为 swarm 预写任何代码**，只保证消息类型编号与信令字段留有余地

## 风险与开放问题

| 风险 | 缓解 |
|---|---|
| 家庭上行 ÷ N，多人接收时人均速度骤降，用户以为坏了 | AD-14：瓶颈说明如实展示「上行已满 ÷ N 人」，这是产品答案而不是技术问题 |
| 发送方句柄/内存 ×N | 上限 8 + 缓存有界 + 9.3 的泄漏测试 |
| 多链路发散导致缓存失效、磁盘读放大 | 可接受的退化（AD-13）；若实测成问题，续传接收方优先级或预读再议 |
| TURN 中继被 N 条链路同时占用，服务器上行成瓶颈 | 已知且如实告知（瓶颈说明「走中继中 × N」）；不做限速，与 V1 决定一致 |
| 网页发送端 N 条 PeerConnection 的浏览器资源上限 | 上限 8 远低于浏览器限制（Chrome 约 500）；10.3 实测确认 |

**开放问题**（不阻塞开工）：

- [ ] 接收方进房是否需要「发送方批准」？V1 哲学是「知道码即可收」，
      一对多下码泄露的后果从「被抢位」变成「多一个下载者」——
      倾向维持不批准（密钥仍在 fragment，服务器与陌生人拿不到内容），待人工复核确认
- [x] `maxReceivers` 上限 8 是否够用？——**已解决：取消固定上限**。
      `MaxReceiversPerRoom` 默认改为不限制，席位数由发送方按自己的上行带宽
      与内存决定；真正的约束是物理的（N 条链路平分一条上行），不是一个常量。
      公开部署想设天花板时把该配置项配成具体数值即可，客户端从 `created`
      的回显里得知生效值。三端输入只保证下界 ≥ 1，不再各自硬夹上界。
