# V2.2.0 网页端流式另存与可选密码 - 变更摘要

## 📅 更新时间
2026-08-25

## 🎯 本次目标

把 FilePizza（BSD-3-Clause）中经实践验证的三个强项能力移植进 NexusP2P，
**全部为可选增强，不开启时行为与 V2.1.0 完全一致**：

1. **Service Worker 流式另存** —— 没有 File System Access API 的浏览器也能流式落盘
2. **多文件一键 ZIP 打包** —— N 个下载链接变成一次点击
3. **可选访问密码** —— 在九位文件码之外加一道进房门槛

---

## ✨ 新功能

### 1. 流式另存（Service Worker）

| 项目 | 说明 |
|------|------|
| 新增文件 | `wwwroot/sw.js`、`wwwroot/stream.html`、`wwwroot/js/storage/stream-saver.js` |
| 来源 | StreamSaver.js（MIT），按同源自部署形态裁剪 |
| 原理 | 页面把 ReadableStream 经 MessageChannel 送进 SW，SW 拦截一次性 URL 把流作为响应体交给下载管理器 |
| 效果 | 「收完之后的拷贝出去」一步内存占用与文件大小**无关**；此前 OPFS / 内存策略要整份读回再给链接 |

适用场景：Firefox、移动浏览器等没有 `showSaveFilePicker` 的环境 ——
此前内存 Blob 策略收 5 GiB 要占 5 GiB 堆，现在收完后流式保存不回内存。

能力探测（AD-6 原则：只探测能力、不判断浏览器型号）新增第 4 行
「流式另存 / 一键 ZIP（Service Worker）」，设置页可见。

### 2. 多文件一键 ZIP

| 项目 | 说明 |
|------|------|
| 新增文件 | `wwwroot/js/core/zip-stream.js` |
| 来源 | FilePizza 的 zip-stream（其原型为 StreamSaver.js 示例），重写为 pull 驱动逐块背压 |
| 格式 | store 模式（不压缩）、UTF-8 文件名、目录项、数据描述符流式写法 |
| 精确大小 | store 模式下归档大小可预先算出并写入 Content-Length，下载进度条有总量 |

行为：OPFS / 内存策略收完多个文件后**自动**开始「打包成单个 ZIP 流式保存」，
一次点击替代逐个点 N 个链接。原有逐文件链接**保留为兜底** ——
SW 不可用、被浏览器拦截或归档超过 4 GiB（zip64 之外）时自动回退，数据不会丢。

空目录也进 zip（保目录结构）；清单里的目录树在归档中完整还原。

### 3. 可选访问密码（信令级）

| 项目 | 说明 |
|------|------|
| 新增文件 | `src/NexusP2P.Signaling/Rooms/RoomPassword.cs` |
| 传输方式 | `?password=` 查询参数随 WSS 入房请求送达（传输中加密），**不进分享链接** |
| 存储 | 服务器只存 PBKDF2-SHA256 校验值（每房独立盐、10 万次迭代），不存明文 |
| 校验 | 任何角色（含发送方重连）都要凭口令进房 —— 宽限期里空出的发送方座位不是旁门 |

安全设计（延续本项目「防枚举预言机」的一贯原则）：

- **密码错误 / 缺失 / 码不存在 / 位子被占，全部返回同一句「房间不可用」** ——
  密码不引入新的枚举通道
- 建房应答回显 `passwordProtected`；旧服务器不认识该参数时，
  网页端**明确警告**「当前信令服务器不支持密码保护，本次传输未设密码」，
  绝不静默降级（与 maxReceivers 回显同一模式）
- 发送端、接收端各有可选密码输入框；留空 = 不设置 = 与从前逐字节一致
- **C# 客户端（CLI / 桌面版）零改动**：不带密码参数即不设置，完全兼容

威胁模型：密码经信令服务器传递并校验，服务器看得到它 —— 这与 V3 起
「密钥由信令协商的连接送达」的既有信任边界一致。传输内容的机密性依赖
AES-256-GCM 而不是密码；密码只挡「拿到文件码但没拿到密码」的人，
因此要与文件码**分开渠道**传递。

---

## 🔧 修复

| 文件 | 问题 |
|------|------|
| `tests/browser.mjs` | 能力探测行数断言过期（3 → 4，随新能力行更新） |
| `tests/e2e.mjs` | 分享链接断言仍在等待 V2.1.0 已移除的 `#` 密钥片段，导致 e2e 超时（主分支遗留问题，本次一并修复） |

## 📦 版本号

- tag：`v2.2.0`（延续 v1.0.0 → v2.1.0 序列）
- `src/NexusP2P.Web/package.json`：2.0.1 → 2.2.0
- `src/NexusP2P.Desktop/NexusP2P.Desktop.csproj`：2.0.1 → 2.2.0
  （打包时由 git 标签覆盖，此处是自动更新比较基准，按注释要求与最新发布版本保持一致）

## ✅ 已修改 / 新增的文件

**服务端（信令）**
- 新增 `Rooms/RoomPassword.cs`
- 修改 `Rooms/Room.cs`（携带口令校验材料）、`Rooms/RoomRegistry.cs`（进房校验）
- 修改 `Signaling/SignalingEndpoints.cs`（password 参数、回显）
- 修改 `Signaling/SignalingMessages.cs`（`passwordProtected` 字段）

**网页端**
- 新增 `wwwroot/sw.js`、`wwwroot/stream.html`
- 新增 `js/storage/stream-saver.js`、`js/core/zip-stream.js`
- 修改 `js/storage/writers.js`（收尾一键保存）、`js/storage/capabilities.js`（第 4 项能力）
- 修改 `js/net/signaling.js`、`js/net/connector.js`、`js/net/fanout-signaling.js`、`js/net/fanout-connector.js`（密码透传）
- 修改 `js/app.js`、`js/ui/dom.js`、`index.html`（两端密码输入、降级警告）

**文档**
- 修改 `docs/formats/signaling.md`（V4 可选口令协议说明）、`README.md`（功能清单）
- 修复两个测试文件的过期断言

## 🔄 配置迁移

**无需任何迁移**。不设密码的房间与 V2.1.0 在 wire 上逐字节一致；
旧客户端（不传 password）建出来的就是无密码房间，新旧服务器 / 客户端任意组合互通。

要使用密码保护，只需网页端发送时在「访问密码（可选）」填入即可。

## 📊 兼容性

| 组合 | 建房带密码 | 建房不带密码 |
|------|-----------|-------------|
| 新网页端 + 新服务器 | ✅ 生效，回显 `passwordProtected` | ✅ 与从前一致 |
| 新网页端 + 旧服务器 | ⚠️ 静默降级 → **界面明确警告** | ✅ 与从前一致 |
| 旧客户端 + 新服务器 | ✅（无密码房间，与从前一致） | ✅ 与从前一致 |

## 🧪 测试与验证

| 项 | 结果 |
|----|------|
| `dotnet build` Release | ✅ 0 警告 0 错误（仓库要求零警告） |
| `dotnet test`（546 项） | ✅ 538 通过；8 项集成测试失败经 **stash 基线对照**确认为本机环境问题（无 .NET 9 运行时、前滚至 10 + WebApplicationFactory），与未改动代码时失败集完全一致 |
| Web `npm test` 全套 | ✅ loopback / cancel / interop（web↔C# 双向逐字节）/ browser / e2e（真实 WebRTC 全链路）全部通过 |
| ZIP 模块专项 | ✅ 二进制结构解析 + crc32 公认值 + .NET `Expand-Archive` 外部解压交叉验证（UTF-8 中文文件名、目录项、SHA256 一致） |

## 🙏 致谢

- [StreamSaver.js](https://github.com/jimmywarting/StreamSaver.js)（MIT）—— 流式下载的 Service Worker 方案
- [FilePizza](https://github.com/kern/filepizza)（BSD-3-Clause）—— zip-stream 与密码保护的实践参考

---

**适用版本**: V2.2.0+
**向后兼容**: ✅ 完全兼容 V2.1.0（wire 逐字节一致，无需迁移）
