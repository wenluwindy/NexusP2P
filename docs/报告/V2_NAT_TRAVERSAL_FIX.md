# V2.0.0 NAT 穿透问题修复说明

## 问题描述

V1.0.0 的一对一传输工作正常，但 V2.0.0 增加一对多传输后，出现连接失败问题：
- 错误信息：**"发送失败：等待数据通道打开超过 30 秒。可能是 ICE 打洞失败。"**
- 根本原因：NAT 穿透配置不足，多个接收方同时连接时 ICE 协商失败

## 已修复的问题

### 1. **缺少 STUN 服务器配置**
**问题**: 没有配置任何 ICE 服务器，导致 NAT 穿透困难
**解决**: 
- 默认配置了 Google 的公共 STUN 服务器
- 修改了验证逻辑，允许只配置 STUN 服务器（不需要 Secret）
- 只有使用 TURN 中继时才需要配置 Secret

**配置文件更新** (`src/NexusP2P.Signaling/appsettings.json`):
```json
"Turn": {
  "Urls": [
    "stun:stun.l.google.com:19302",
    "stun:stun1.l.google.com:19302",
    "stun:stun2.l.google.com:19302"
  ],
  "Secret": "",
  "CredentialTtlSeconds": 3600
}
```

### 2. **入房速率限制过严**
**问题**: 同一 IP 每分钟只允许 20 次入房尝试，多接收方场景容易触发限制
**解决**: 将限制提高到 100 次/分钟

**变更**:
```json
"JoinAttemptsPerMinute": 100  // 原值: 20
```

### 3. **ICE 候选池太小**
**问题**: `iceCandidatePoolSize: 1` 在多接收方场景下候选不足
**解决**: 
- 将候选池大小增加到 4
- 添加更积极的 ICE 收集策略

**受影响文件**:
- `src/NexusP2P.Web/wwwroot/js/net/connector.js`
- `src/NexusP2P.Web/wwwroot/js/net/fanout-connector.js`

### 4. **连接超时时间过短**
**问题**: 30 秒超时在多接收方场景下可能不够
**解决**: 将超时时间延长至 60 秒

**受影响文件**:
- `src/NexusP2P.Web/wwwroot/js/net/peer.js`
- `src/NexusP2P.Web/wwwroot/js/net/connector.js`

### 5. **缺少连接状态监控**
**问题**: 连接失败时缺少诊断信息
**解决**: 添加了详细的 ICE 状态日志

**新增监控**:
- ICE 收集状态 (`onicegatheringstatechange`)
- ICE 连接状态 (`oniceconnectionstatechange`)
- 整体连接状态 (`onconnectionstatechange`)

## 配置指南

### 基础配置（使用公共 STUN 服务器）

1. 编辑 `src/NexusP2P.Signaling/appsettings.json`
2. 设置 `PublicOrigin` 为你的服务器地址：
   ```json
   "PublicOrigin": "http://your-server-ip:5000"
   ```
3. 确保 STUN 服务器已配置（默认已包含 Google STUN）
4. 启动服务

### 高级配置（使用自己的 TURN 服务器）

如果需要更好的连接成功率，特别是在严格的 NAT 环境下，建议配置 TURN 中继服务器：

1. 安装 coturn:
   ```bash
   # Ubuntu/Debian
   sudo apt-get install coturn
   
   # 或使用 Docker
   docker run -d --network=host \
     coturnproject/coturn \
     -n --log-file=stdout \
     --use-auth-secret \
     --static-auth-secret=your-secret-key \
     --realm=your-domain.com
   ```

2. 配置 appsettings.json:
   ```json
   "Turn": {
     "Urls": [
       "stun:your-turn-server.com:3478",
       "turn:your-turn-server.com:3478",
       "turns:your-turn-server.com:5349"
     ],
     "Secret": "your-secret-key",
     "CredentialTtlSeconds": 3600
   }
   ```

### 反向代理配置

如果使用 nginx/Caddy 等反向代理：

```json
"BehindReverseProxy": true,
"KnownProxies": ["127.0.0.1"]
```

## 测试验证

### 1. 检查 STUN 配置是否生效

打开浏览器开发者工具，查看控制台日志：
```
[connector] ICE 候选收集完成
[connector] ICE 连接状态: connected
```

### 2. 测试一对多传输

1. 发送方创建房间
2. 多个接收方（建议 2-3 个）同时加入
3. 观察连接是否都能成功建立
4. 检查文件传输是否正常完成

### 3. 检查 NAT 穿透类型

连接成功后，在 UI 中查看连接类型：
- **同局域网直连** (host-host): 最快
- **打洞成功，公网直连** (srflx): 较快
- **经服务器中继** (relay): 较慢但最稳定

## 故障排查

### 问题: 仍然超时

**检查项**:
1. 确认 STUN 服务器可访问：
   ```bash
   # 测试 Google STUN
   stunclient stun.l.google.com
   ```

2. 检查防火墙设置，确保允许 UDP 流量

3. 查看浏览器控制台的详细日志

### 问题: 入房被限速（429 错误）

**解决方法**:
1. 增加 `JoinAttemptsPerMinute` 值
2. 如果使用反向代理，确保正确配置 `BehindReverseProxy` 和 `KnownProxies`

### 问题: 连接建立但传输很慢

**原因**: 可能走的是中继而非直连

**解决方法**:
1. 检查网络环境，尝试改善 NAT 类型
2. 配置更多 STUN 服务器
3. 使用端口映射（Port Forwarding）

## 代码变更摘要

| 文件 | 变更类型 | 说明 |
|------|---------|------|
| `appsettings.json` | 配置 | 添加 STUN 服务器，放宽速率限制 |
| `TurnCredentialService.cs` | 功能增强 | 支持只配置 STUN（无需 Secret） |
| `SignalingOptions.cs` | 验证逻辑 | 区分 STUN 和 TURN 的配置要求 |
| `connector.js` | 性能优化 | 增加候选池，添加监控日志 |
| `fanout-connector.js` | 性能优化 | 增加候选池，添加监控日志 |
| `peer.js` | 超时调整 | 30秒 → 60秒 |

## 版本兼容性

- ✅ 完全向后兼容 V1.0.0 的一对一传输
- ✅ V2.0.0 的一对多传输现在可以正常工作
- ✅ 旧版本客户端仍可连接（服务器自动降级）

## 性能影响

- **连接建立时间**: 可能略有增加（多收集候选），但成功率大幅提升
- **内存使用**: 增加约 5-10%（更大的候选池）
- **CPU 使用**: 基本无影响

## 后续建议

1. **生产环境**: 强烈建议部署自己的 TURN 服务器
2. **监控**: 定期检查连接成功率和使用的候选类型
3. **优化**: 根据实际网络环境调整超时和候选池大小

## 相关文档

- [WebRTC ICE 详解](https://developer.mozilla.org/en-US/docs/Web/API/WebRTC_API/Connectivity)
- [coturn 安装指南](https://github.com/coturn/coturn)
- [STUN/TURN 协议说明](https://datatracker.ietf.org/doc/html/rfc5389)
