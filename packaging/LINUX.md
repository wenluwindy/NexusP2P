# 信令服务器 — Linux 部署 (V2.0.0)

一个自包含的可执行文件，**服务器上不需要装 .NET**。

```
nexusp2p-signaling            信令服务器本体（自包含 ELF）
wwwroot/                      网页版界面（浏览器直接访问域名就能收发）
appsettings.json              配置，★ 标出必改项。包里只有这一份
nexusp2p-signaling.service    systemd 单元
nginx-nexusp2p.conf           nginx 反向代理配置（443 已被占用时用）
docker/                       Dockerfile 与 compose（容器化部署用）
部署说明.md                    这份文档（裸机 / 通用 nginx）
1Panel部署说明.md              用 1Panel 面板部署看这份
```

## V2.0.0 新特性

### 1. STUN 服务器本地化（中国区优化）
默认配置了国内可访问的 STUN 服务器：
- 小米: `stun:stun.miwifi.com:3478`
- 哔哩哔哩: `stun:stun.chat.bilibili.com:3478`
- 湖南卫视: `stun:stun.hitv.com:3478`

**中国区用户无需修改**，开箱即用。

### 2. 速率限制可选化
新增 `EnableJoinRateLimit` 开关：
- 生产环境：建议 `true`（默认）
- 内网环境：可设为 `false`

### 3. 一对多传输支持
支持一个发送方对多个接收方（2-5个）同时传输。

---

信令服务器**只转发 SDP 与 ICE 候选，不碰文件字节**，也完全无状态 ——
房间只在内存里，重启全丢也不影响正确性（续传锚在内容哈希上，不是房间号）。

它同时**托管网页版界面**：部署好之后浏览器打开 `https://你的域名` 就能直接
收发文件，对方不用装任何东西。分享链接（`https://你的域名/r/<码>#<密钥>`）
点开就是这个界面。

> `wwwroot/` 是可选的：删掉它信令照样工作，只是网页界面变成 404。
> 想只提供信令、不提供网页界面时可以这么做。

**网页版与桌面版能力不同，这是浏览器的限制，不是没做完**：

| | 桌面版 exe | 网页版 |
|---|---|---|
| 传输速度 | 相同（同一套协议与加密） | 相同 |
| 大文件 | 无上限，直接写磁盘 | 取决于浏览器能力，见下 |
| 关掉界面后继续传 | 可以（缩到托盘） | 不行，关标签页即中断 |
| 跨会话续传 | 可以（进度记在磁盘上） | 不行，每次从头开始 |

网页版会在开始接收之前**探测本浏览器的落盘能力并如实告知上限**：
Chrome / Edge 能直接写进你选的文件夹（无实际上限）；Firefox 没有这个 API，
会退到浏览器存储或内存，大文件有失败风险。10 GB 以上建议用桌面版。

---

## 一、放上去

```bash
tar -xzf nexusp2p-signaling-linux-x64.tar.gz
sudo mkdir -p /opt/nexusp2p-signaling /var/lib/nexusp2p-signaling
sudo cp nexusp2p-signaling-linux-x64/nexusp2p-signaling /opt/nexusp2p-signaling/
sudo cp nexusp2p-signaling-linux-x64/appsettings.json /opt/nexusp2p-signaling/
sudo chmod +x /opt/nexusp2p-signaling/nexusp2p-signaling
```

> 包是在 Windows 上打的，NTFS 没有可执行位，所以 tar 里所有文件都被统一标成
> 0755。`chmod +x` 那一行是兜底 —— 万一你的 tar 解出来不带执行位，
> 少了它就是 `Permission denied`。

## 二、改配置

编辑 `/opt/nexusp2p-signaling/appsettings.json`（**包里只有这一份配置**，
不用纠结 `appsettings.Production.json` —— 那种多份覆盖的结构最容易改错文件），
必改项标了 ★：

| 项 | 改成什么 | 改错了会怎样 |
|---|---|---|
| `PublicOrigin` | 对外的完整基址，如 `https://p2p.你的域名` | 生成的分享链接对方打不开。**缺了会直接拒绝启动**（刻意如此） |
| `BehindReverseProxy` | 走 nginx / Caddy 就填 `true`，直接绑 443 就留 `false` | **填错会让入房限速失效**，见下 |
| `EnableJoinRateLimit` | V2.0.0 新增。生产环境 `true`，内网可用 `false` | 控制是否启用速率限制 |

> **`BehindReverseProxy` 是这次部署最容易踩的一项。**
>
> 填 `false` 却在代理后面：服务端看到的来源 IP 全是 `127.0.0.1`，
> 所有人共用一个配额 —— 几十次入房尝试后**全体被 429**，
> 而真想枚举文件码的人也一样只占大家共用的那份。
>
> 填 `true` 却没有代理：`X-Forwarded-For` 是客户端能随便写的头，
> 伪造一下就绕过限速了。**所以它默认关着，而且必须显式打开。**

### V2.0.0 配置说明

**STUN 服务器**（无需修改）：
```json
"Turn": {
  "Urls": [
    "stun:stun.miwifi.com:3478",
    "stun:stun.chat.bilibili.com:3478",
    "stun:stun.hitv.com:3478",
    "stun:stun.voipgate.com:3478"
  ]
}
```
- 中国区用户默认配置已优化
- 海外用户可改用 Google STUN: `stun:stun.l.google.com:19302`

**速率限制**（按需修改）：
```json
"EnableJoinRateLimit": true,      // 内网可改为 false
"JoinAttemptsPerMinute": 100      // V2.0.0 已提高到 100
```

### 配了 TURN 密钥之后收一下权限

`Turn:Secret` 是与 coturn 共享的密钥。它写进配置文件之后，那个文件就不该
所有人可读了：

```bash
sudo chown nexusp2p:nexusp2p /opt/nexusp2p-signaling/appsettings.json
sudo chmod 600 /opt/nexusp2p-signaling/appsettings.json
```

密钥泄露的后果是任何人都能白嫖你家的中继带宽，而且换密钥要重启服务、
所有在传的都会断。

## 三、跑起来

### 方案 A：走现有的 nginx（推荐，443 已被网站占着就选这个）

```bash
sudo cp nexusp2p-signaling-linux-x64/nginx-nexusp2p.conf /etc/nginx/conf.d/
sudo vim /etc/nginx/conf.d/nginx-nexusp2p.conf    # 改域名和证书路径
sudo nginx -t && sudo systemctl reload nginx
```

配置里有三处**不能删**的东西，删了就不通：

- `proxy_set_header Upgrade` / `Connection "upgrade"` —— 少了 WebSocket
  完全不通，但 `/health` 一切正常，很容易误判成程序问题
- `X-Forwarded-For` —— 限速要靠它（同时配置里 `BehindReverseProxy` 得是 `true`）
- `proxy_read_timeout 7d` —— nginx 默认 60 秒无数据就断，而信令在传输开始后
  基本是静默的，默认值下必然被掐

配置里的 `BehindReverseProxy` 记得改成 `true`。

### 方案 B：直接绑 443

443 没被占用时更简单，但要自己配证书：

```json
"Kestrel": {
  "Endpoints": {
    "Https": {
      "Url": "https://0.0.0.0:443",
      "Certificate": {
        "Path": "/etc/letsencrypt/live/p2p.你的域名/fullchain.pem",
        "KeyPath": "/etc/letsencrypt/live/p2p.你的域名/privkey.pem"
      }
    }
  }
}
```

同时把 systemd 单元里的 `--urls` 去掉，并解注释 `AmbientCapabilities=CAP_NET_BIND_SERVICE`
（非 root 绑 443 需要它）。

> 80 端口被封，所以证书要用 **DNS-01** 签发，不能用 HTTP-01。
> 见仓库 `deploy/README.md`。

### systemd

```bash
sudo useradd --system --no-create-home --shell /usr/sbin/nologin nexusp2p
sudo chown -R nexusp2p:nexusp2p /var/lib/nexusp2p-signaling

sudo cp nexusp2p-signaling-linux-x64/nexusp2p-signaling.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now nexusp2p-signaling
```

## 四、验证

```bash
# 本机
curl -s http://127.0.0.1:5000/health

# 走代理之后（从外网）
curl -s https://p2p.你的域名/health
```

应该看到：

```json
{
  "status": "ok",
  "activeRooms": 0,
  "publicOrigin": "https://p2p.你的域名",
  "relayConfigured": false,
  "behindReverseProxy": true
}
```

**四个字段都要核对一遍**，它们分别对应四类配错：

| 字段 | 不对的话 |
|---|---|
| `publicOrigin` | 与实际域名不一致 → 分享链接对方打不开 |
| `behindReverseProxy` | 与实际部署形态不符 → 限速失效或可被伪造绕过 |
| `relayConfigured` | `false` 表示没配 TURN → 跨公网打洞失败就连不上 |
| `activeRooms` | 建房后应该变成 1，一直是 0 说明 WebSocket 没通 |

### WebSocket 是否真的通

`/health` 是普通 HTTP，**它正常不代表 WebSocket 通**。真正的验证是拿客户端连一次：

```
nexusp2p.exe send 随便一个文件 --signaling https://p2p.你的域名
```

能打出文件码就说明 WebSocket 建房成功了。打不出来先看 nginx 的
`Upgrade` 两行是否配了。

## 五、日常

```bash
sudo systemctl restart nexusp2p-signaling    # 改配置后
journalctl -u nexusp2p-signaling -f          # 看日志
```

配置在**启动时**校验并一次报出全部问题，不会带着错配置跑起来。
所以改完配置重启失败的话，`journalctl` 里那一段就是原因。

## 六、V2.0.0 故障排查

### 连接失败：等待数据通道打开超时

**症状**: "等待数据通道打开超过 60 秒。可能是 ICE 打洞失败。"

**原因**:
1. STUN 服务器不可达
2. 防火墙阻止 UDP 流量
3. 网络环境不支持 WebRTC

**解决方法**:
```bash
# 1. 检查 STUN 配置
grep -A 5 '"Urls"' /opt/nexusp2p-signaling/appsettings.json

# 2. 测试 STUN 服务器（需要安装 stuntman）
stunclient stun.miwifi.com 3478

# 3. 检查防火墙
sudo ufw status
sudo iptables -L -n | grep UDP

# 4. 临时关闭防火墙测试
sudo ufw disable
# 测试后记得重新开启
sudo ufw enable
```

### 429 Too Many Requests

**症状**: 客户端报 429 错误，无法入房

**原因**: 触发速率限制

**解决方法**:

**内网环境**（推荐）:
```json
"EnableJoinRateLimit": false
```

**公网环境**（增加限制）:
```json
"EnableJoinRateLimit": true,
"JoinAttemptsPerMinute": 200
```

重启服务:
```bash
sudo systemctl restart nexusp2p-signaling
```

### 中国区 STUN 服务器优化

**海外部署**改用 Google STUN:
```json
"Turn": {
  "Urls": [
    "stun:stun.l.google.com:19302",
    "stun:stun1.l.google.com:19302"
  ]
}
```

**测试 STUN 连通性**:
```bash
# 使用在线工具
curl -s https://webrtc.github.io/samples/src/content/peerconnection/trickle-ice/

# 或浏览器访问该地址，手动测试
```

### 一对多传输失败

**症状**: 第一个接收方成功，后续失败

**解决方法**:
```json
// 确保这些配置正确
"EnableJoinRateLimit": false,     // 或设置足够大的值
"MaxReceiversPerRoom": 2147483647 // 不限制
```

**让接收方间隔加入**:
- 每个接收方间隔 5-10 秒加入
- 避免同时并发连接

### 查看详细日志

```bash
# 实时查看日志
journalctl -u nexusp2p-signaling -f

# 查看最近 100 行
journalctl -u nexusp2p-signaling -n 100

# 查看错误日志
journalctl -u nexusp2p-signaling -p err

# 过滤 ICE 相关日志
journalctl -u nexusp2p-signaling | grep -i "ice\|stun\|turn"
```

## 七、还没验证的部分

诚实记录：

- **这份 Linux 包我只做了构建，没有在 Linux 上跑过。**
  代码本身是跨平台的（纯 ASP.NET Core，无 Windows 专有 API），
  自包含单文件也不依赖系统 .NET，但「构建成功」不等于「跑起来了」。
  第一次起服务请把 `journalctl` 的输出看完。
- **中继（coturn）路径一次真实数据都没跑过。** 见 `deploy/README.md`。
- nginx 配置里的超时与缓冲设置是按信令的特性定的，但没在真实长传输
  （50 分钟以上）里验证过。
- **V2.0.0 的一对多传输功能已测试通过**，但在生产环境的大规模使用尚未验证。
