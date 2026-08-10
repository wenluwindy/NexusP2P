# NexusP2P

NexusP2P 是一个面向大文件和文件夹的点对点传输平台。它使用 WebRTC DataChannel 在发送端与接收端之间传输数据，信令服务器只负责配对双方并转发 SDP/ICE 协商消息，不保存或解析文件内容。

项目提供 Windows 桌面客户端、命令行客户端和可自部署的信令服务器，支持断点续传、断线重连、分片校验和端到端加密。

> 当前项目仍处于持续开发阶段。发布包位于 [`dist/`](dist/)，部署相关文件位于 [`packaging/`](packaging/) 和 [`deploy/`](deploy/)。

## 功能特性

- P2P 直连传输，支持局域网直连、公网打洞和 TURN 中继
- 支持单个文件或整个文件夹、分享链接或九位文件码
- AES-256-GCM 内容加密，SHA-256 Merkle 树和分片级校验
- 基于本地 `.part` / `.meta` 状态的断点续传
- 网络波动或进程重启后自动重连和继续传输
- 文件名和目录路径安全校验，避免路径穿越
- Windows WPF 桌面客户端，支持拖放、托盘运行和进度显示
- CLI 客户端，适合服务器、脚本和自动化任务
- 原生 ES Module 网页端，无前端构建步骤
- Docker、systemd、Nginx 和 1Panel 部署文件

## 工作原理

```text
发送端 ── WebSocket ──► 信令服务器 ◄── WebSocket ── 接收端
   │                                                  │
   └──────────── WebRTC DataChannel 直连/中继 ─────────┘
                         文件数据
```

信令服务器只传递连接建立所需的信息。分享链接的密钥位于 URL 的 `#fragment` 中，不会随 HTTP/WebSocket 请求发送到服务器，因此即使连接经过 TURN 中继，服务器也无法解密文件内容。

## 快速开始

### 环境要求

- .NET SDK 9.0 或更高版本
- Node.js 20 或更高版本（运行网页端测试时需要）
- Windows 桌面客户端需要 Windows 和 .NET 9 Desktop Runtime

### 启动信令服务器

配置 `Signaling:PublicOrigin`，它用于生成分享链接：

```json
{
  "Signaling": {
    "PublicOrigin": "https://p2p.example.com"
  }
}
```

开发环境运行：

```powershell
dotnet run --project src/NexusP2P.Signaling
```

健康检查地址为 `GET /health`。生产部署请参考 [`packaging/LINUX.md`](packaging/LINUX.md)、[`packaging/1PANEL.md`](packaging/1PANEL.md) 和 [`packaging/docker/`](packaging/docker/)。

### 使用 Windows 桌面客户端

```powershell
dotnet run --project src/NexusP2P.Desktop
```

在“设置”中填写信令服务器地址。发送方选择文件或文件夹后生成文件码/分享链接，将分享链接发送给接收方；接收方打开链接或输入文件码和密钥，选择目标目录后开始接收。

### 使用命令行客户端

发送文件或文件夹：

```powershell
dotnet run --project src/NexusP2P.Cli -- send .\example.iso --signaling https://p2p.example.com
```

接收分享链接：

```powershell
dotnet run --project src/NexusP2P.Cli -- receive "https://p2p.example.com/123456789#密钥" --dest .\downloads --signaling https://p2p.example.com
```

使用文件码和密钥接收：

```powershell
dotnet run --project src/NexusP2P.Cli -- receive 123456789 --key "密钥" --dest .\downloads --signaling https://p2p.example.com
```

信令地址的优先级为：`--signaling` 参数、`NEXUSP2P_SIGNALING` 环境变量、可执行文件旁的 `nexusp2p.json`。查看完整用法：`dotnet run --project src/NexusP2P.Cli -- --help`。

## 构建和测试

```powershell
dotnet restore NexusP2P.sln
dotnet build NexusP2P.sln --configuration Release
dotnet test NexusP2P.sln --configuration Release
```

网页端测试：

```powershell
cd src/NexusP2P.Web
npm test
```

网页端是原生 ES Module，不需要 Webpack、Vite 或其他打包器。信令项目构建时会把 `src/NexusP2P.Web/wwwroot` 复制到输出目录。

## 发布和部署

- [`packaging/package.sh`](packaging/package.sh)：生成发布包
- [`packaging/package-dll.sh`](packaging/package-dll.sh)：生成 framework-dependent 包
- [`packaging/docker/`](packaging/docker/)：Dockerfile 与 Docker Compose
- [`packaging/nexusp2p-signaling.service`](packaging/nexusp2p-signaling.service)：systemd 服务
- [`packaging/nginx-nexusp2p.conf`](packaging/nginx-nexusp2p.conf)：Nginx 反向代理示例
- [`packaging/start-signaling.ps1`](packaging/start-signaling.ps1) / `.cmd`：启动脚本

手动发布示例：

```powershell
dotnet publish src/NexusP2P.Signaling/NexusP2P.Signaling.csproj `
  --configuration Release `
  --runtime linux-x64 `
  --self-contained true `
  --output .\artifacts\signaling-linux-x64
```

生产环境应使用 HTTPS，显式配置 `Signaling:PublicOrigin`，跨网络传输时配置 STUN/TURN，并确保反向代理支持 WebSocket 升级。

## 项目结构

- `src/NexusP2P.Core`：清单、路径、哈希、Merkle、加密和文件码
- `src/NexusP2P.Transport.*`：数据通道抽象及 WebRTC 实现
- `src/NexusP2P.Transfer`：帧协议、分片存储、发送/接收会话和重连
- `src/NexusP2P.Agent`：传输编排、配置和设置
- `src/NexusP2P.Signaling`：ASP.NET Core 信令服务器和网页托管
- `src/NexusP2P.Cli`：命令行客户端
- `src/NexusP2P.Desktop`：Windows WPF 客户端
- `tests`：核心、传输、Agent、集成和跨进程测试
- `docs`：协议、架构决策和验证记录
- `packaging` / `deploy`：发布和部署文件
- `spike`：WebRTC 实现和吞吐量验证实验

## 设计与协议文档

- [传输协议](docs/formats/protocol.md)
- [信令协议](docs/formats/signaling.md)
- [Wire 格式、文件码和分享链接](docs/formats/wire.md)
- [哈希和 Merkle 校验](docs/formats/hashing.md)
- [WebRTC 实现决策](docs/adr/001-webrtc-implementation.md)
- [网络约束](docs/spikes/network-constraints.md)
- [校园网 AP 隔离验证](docs/spikes/campus-ap-isolation.md)
- [浏览器存储能力验证](docs/spikes/browser-storage.md)

## 安全边界

- 服务端不保存文件字节，也不参与文件内容解密。
- 分享链接中的 `#fragment` 包含传输密钥，请像密码一样保护完整链接。
- 文件内容使用 AES-256-GCM；分片位置参与 nonce 派生，收到的数据还会经过 Merkle 校验。
- 接收端会校验清单中的路径，防止写入目标目录之外。
- 信令房间和文件码不是身份认证机制；生产环境应使用 HTTPS、限速和房间上限。
- TURN 中继可以看到网络流量元数据，但没有传输密钥，无法读取文件内容。

## 已知限制

- 当前桌面客户端面向 Windows；跨平台主要通过 CLI 和信令服务实现。
- P2P 是否直连取决于双方网络环境；直连失败时需要可用的 TURN 服务。
- 信令房间保存在内存中，服务重启会清空房间；已落盘的分片进度不依赖房间，可重新发起传输继续。
- 中继模式的实际速度受 TURN 服务器上行带宽限制。
- `dist/` 中的预构建产物可能不是每次源码修改后的最新版本，源码构建是最可靠的验证方式。

## 贡献

欢迎提交 Issue 和 Pull Request。涉及协议、加密、断点续传、路径处理或信令安全的修改，请同时补充测试，并更新 `docs/` 下对应的协议或架构文档。

## 许可证

本仓库当前未包含许可证文件。公开到 GitHub 前，请根据你的分发和开源计划添加 `LICENSE`，否则其他人默认不拥有复制、修改或再分发代码的许可。
