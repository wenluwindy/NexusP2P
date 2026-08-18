# V2.0.0 快速故障排查卡

## 🔴 错误：等待数据通道打开超过 60 秒

### 快速检查清单

```
□ STUN 服务器是否已配置？
  ├─ 查看 appsettings.json 中的 Turn.Urls
  └─ 应该有至少 1 个 stun: 开头的地址

□ 浏览器控制台有什么日志？
  ├─ F12 打开开发者工具
  ├─ 查看 Console 标签
  └─ 搜索 "ICE" 关键字

□ 防火墙是否阻止 UDP？
  └─ 临时关闭防火墙测试

□ 网络环境是否支持 WebRTC？
  └─ 访问 https://test.webrtc.org/ 测试
```

## 🟡 错误：429 Too Many Requests

### 原因
同一 IP 入房尝试次数超限

### 解决方法
```json
// appsettings.json
"JoinAttemptsPerMinute": 200  // 增加限制
```

## 🟢 连接成功但速度很慢

### 检查连接类型

在浏览器界面查看：
- ✅ **同局域网直连** → 正常，最快
- ⚠️ **打洞成功，公网直连** → 正常，受带宽限制
- 🐌 **经服务器中继** → 慢，需要配置 TURN 服务器

### 改善方法
1. 检查网络环境，改善 NAT 类型
2. 使用端口映射（UPnP 或手动配置）
3. 配置专用 TURN 服务器

## 📋 配置检查命令

### Windows PowerShell
```powershell
# 查看当前配置
Get-Content src\NexusP2P.Signaling\appsettings.json | ConvertFrom-Json | 
  Select-Object -ExpandProperty Signaling | Format-List

# 快速更新配置
.\update-config-for-v2.ps1 -PublicOrigin "http://your-ip:5000"

# 测试 STUN 服务器（需要安装 stuntman）
stunclient stun.l.google.com
```

### Linux/Mac
```bash
# 查看配置
cat src/NexusP2P.Signaling/appsettings.json | jq .Signaling

# 测试 STUN 服务器
nc -u stun.l.google.com 19302
```

## 🔍 日志关键字速查

### ✅ 成功的标志
```
[connector] ICE 连接状态: connected
[fanout] ICE 连接成功 peerId=
连接状态: connected
```

### ❌ 失败的标志
```
ICE 连接失败
state=failed
等待数据通道打开超过
connection timeout
```

### ⚠️ 警告但可能正常
```
ICE 连接状态: checking  ← 正在尝试，等待即可
onicegatheringstatechange  ← 收集候选中
```

## 🛠️ 紧急修复步骤

### 步骤 1: 确保 STUN 已配置（5 分钟）

编辑 `src/NexusP2P.Signaling/appsettings.json`:
```json
"Turn": {
  "Urls": [
    "stun:stun.l.google.com:19302"
  ]
}
```

### 步骤 2: 重启服务
```bash
cd src/NexusP2P.Signaling
dotnet run
```

### 步骤 3: 清除浏览器缓存
```
Ctrl + Shift + Delete → 清除缓存 → 刷新页面
```

### 步骤 4: 重新测试
1. 发送方创建房间
2. 接收方加入（间隔 5 秒）
3. 观察浏览器控制台日志

## 📞 需要帮助时提供的信息

```
1. 操作系统和版本
   示例：Windows 11 / Ubuntu 22.04 / macOS 14

2. 浏览器和版本
   示例：Chrome 120 / Firefox 121 / Edge 120

3. 网络环境
   □ 同一局域网  □ 跨公网  □ 移动网络

4. appsettings.json 的 Signaling 部分
   （删除敏感信息后）

5. 浏览器控制台的完整日志
   （特别是包含 "ICE" 或 "error" 的行）

6. 服务器日志
   （dotnet run 的输出）
```

## 🎯 最常见的 3 个错误及修复

| # | 错误 | 修复 | 耗时 |
|---|------|------|------|
| 1 | 没配置 STUN | 添加 Google STUN 到 Turn.Urls | 2 分钟 |
| 2 | PublicOrigin 未设置 | 设置为服务器真实地址 | 1 分钟 |
| 3 | 速率限制过严 | JoinAttemptsPerMinute → 100 | 1 分钟 |

## 🚀 性能调优建议

### 适合大多数场景的配置
```json
{
  "Signaling": {
    "PublicOrigin": "http://your-server:5000",
    "JoinAttemptsPerMinute": 100,
    "RoomGracePeriodSeconds": 60,
    "MaxRooms": 1000,
    "MaxReceiversPerRoom": 2147483647,
    "Turn": {
      "Urls": [
        "stun:stun.l.google.com:19302",
        "stun:stun1.l.google.com:19302"
      ]
    }
  }
}
```

### 高并发场景（> 10 个接收方）
```json
"JoinAttemptsPerMinute": 500,
"MaxReceiversPerRoom": 50
```

### 低延迟优先
```javascript
// 客户端代码中
iceCandidatePoolSize: 8  // 更多候选
```

## 📚 延伸阅读

- `V2_修复总结.md` - 完整的中文修复说明
- `V2_NAT_TRAVERSAL_FIX.md` - 详细的英文技术文档
- `TESTING_V2.md` - 完整的测试指南

## ⏱️ 预期时间线

- 配置更新：5 分钟
- 重新编译：2 分钟
- 基础测试：5 分钟
- 完整测试：15-30 分钟

**总计：约 30 分钟即可验证修复是否成功**

---

💡 **提示**: 大多数问题都是配置问题，不是代码 bug。按照本卡片的步骤，99% 的问题都能解决！
