# V2.0.0 一对多传输测试指南

## 快速测试步骤

### 1. 更新配置并启动服务

```bash
# Windows
.\update-config-for-v2.ps1 -PublicOrigin "http://localhost:5000"
cd src\NexusP2P.Signaling
dotnet run

# Linux/Mac
cd src/NexusP2P.Signaling
# 手动编辑 appsettings.json，设置 PublicOrigin
dotnet run
```

### 2. 打开多个浏览器窗口

建议使用 3 个不同的浏览器窗口或隐身模式：
- 窗口 A：发送方
- 窗口 B：接收方 1
- 窗口 C：接收方 2

### 3. 发送方操作（窗口 A）

1. 访问 `http://localhost:5000`
2. 选择要发送的文件
3. 点击"创建房间"或"开始发送"
4. **重要**: 在"接收方数量"字段输入 `2` 或更多
5. 记下生成的文件码（9 位数字）
6. **打开浏览器开发者工具**（F12），查看控制台日志

### 4. 接收方操作（窗口 B 和 C）

**同时或依次**在两个窗口中：
1. 访问 `http://localhost:5000`
2. 输入发送方提供的文件码
3. 点击"加入房间"或"开始接收"
4. 观察连接建立过程

### 5. 观察日志输出

#### 正常情况下应该看到的日志：

**发送方控制台**:
```
[fanout] ICE 连接成功 peerId=a1b2c3d4, state=connected
[fanout] ICE 连接成功 peerId=e5f6g7h8, state=connected
```

**接收方控制台**:
```
[connector] ICE 候选收集完成
[connector] ICE 连接状态: checking
[connector] ICE 连接状态: connected
[connector] 连接状态: connected
```

#### 如果看到以下日志，说明仍有问题：

```
[fanout] ICE 连接失败 peerId=..., state=failed
[connector] ICE 连接失败，可能是 NAT 穿透失败
等待数据通道打开超过 60 秒。可能是 ICE 打洞失败。
```

## 验证连接类型

连接成功后，界面上应显示连接类型：
- ✅ **同局域网直连** - 最佳情况
- ✅ **打洞成功，公网直连** - STUN 生效
- ⚠️ **经服务器中继** - 需要配置 TURN
- ❌ **连接类型未知** - 可能存在问题

## 性能测试

### 小文件测试（推荐新手）
- 文件大小: 1-10 MB
- 预期时间: 几秒到十几秒
- 目的: 验证连接建立和基本传输

### 大文件测试（验证稳定性）
- 文件大小: 100 MB - 1 GB
- 预期时间: 取决于网络速度
- 目的: 验证长时间传输的稳定性

### 多接收方压力测试
- 接收方数量: 3-5 个
- 文件大小: 50 MB
- 目的: 验证并发传输能力

## 常见问题排查

### 问题 1: 第一个接收方成功，后续失败

**症状**:
```
第一个接收方：✅ 连接成功
第二个接收方：❌ 超时
```

**可能原因**:
1. 速率限制太严格
2. 发送方网络带宽不足
3. 防火墙阻止多个并发 UDP 连接

**解决方法**:
```json
// 增加速率限制
"JoinAttemptsPerMinute": 200

// 让接收方间隔 5-10 秒加入
```

### 问题 2: 所有接收方都超时

**症状**:
```
所有接收方：❌ 等待数据通道打开超过 60 秒
```

**可能原因**:
1. STUN 服务器不可达
2. UDP 端口被防火墙阻止
3. 网络环境不支持 WebRTC

**解决方法**:

1. 测试 STUN 服务器连通性:
```bash
# 使用在线工具测试
# https://webrtc.github.io/samples/src/content/peerconnection/trickle-ice/

# 或使用命令行（需要安装 stuntman）
stunclient stun.l.google.com
```

2. 检查防火墙设置:
```bash
# Windows: 允许 UDP 流量
New-NetFirewallRule -DisplayName "WebRTC UDP" -Direction Inbound -Protocol UDP -Action Allow

# Linux: 检查 iptables
sudo iptables -L -n -v | grep UDP
```

3. 配置 TURN 中继服务器（见下文）

### 问题 3: 连接很慢

**症状**: 连接建立需要 30-60 秒

**解决方法**:
- 检查是否走的是中继（relay）
- 尝试改善网络环境
- 增加更多 STUN 服务器

## 配置 TURN 中继服务器（高级）

如果多次测试都失败，建议配置 TURN 服务器：

### 使用 Docker 快速部署 coturn

```bash
docker run -d --name coturn \
  --network=host \
  coturnproject/coturn \
  -n \
  --log-file=stdout \
  --listening-port=3478 \
  --fingerprint \
  --lt-cred-mech \
  --use-auth-secret \
  --static-auth-secret=my-secret-key \
  --realm=my-domain.com \
  --user=username:password
```

### 更新 appsettings.json

```json
"Turn": {
  "Urls": [
    "stun:your-server-ip:3478",
    "turn:your-server-ip:3478"
  ],
  "Secret": "my-secret-key",
  "CredentialTtlSeconds": 3600
}
```

## 生产环境检查清单

- [ ] `PublicOrigin` 配置正确（不是 localhost）
- [ ] STUN/TURN 服务器可从公网访问
- [ ] 防火墙允许 UDP 3478 端口
- [ ] `JoinAttemptsPerMinute` 设置合理（建议 100+）
- [ ] 如使用反向代理，正确配置 `BehindReverseProxy`
- [ ] 测试不同网络环境（家庭 WiFi、4G、公司网络）
- [ ] 监控连接成功率和类型分布

## 性能基准

### 理想情况（局域网直连）
- 连接建立时间: < 2 秒
- 传输速度: 接近物理带宽（100-1000 Mbps）
- 同时支持接收方: 5-10 个（取决于上行带宽）

### 公网直连（STUN 打洞成功）
- 连接建立时间: 3-10 秒
- 传输速度: 取决于两端较慢的带宽（通常是上行）
- 同时支持接收方: 2-5 个

### TURN 中继
- 连接建立时间: 5-15 秒
- 传输速度: 受 TURN 服务器带宽限制
- 同时支持接收方: 1-3 个

## 需要更多帮助？

查看详细文档：
- `V2_NAT_TRAVERSAL_FIX.md` - 完整的修复说明
- [WebRTC Troubleshooting](https://webrtc.org/getting-started/testing)
- [ICE Connection 诊断工具](https://webrtc.github.io/samples/src/content/peerconnection/trickle-ice/)
