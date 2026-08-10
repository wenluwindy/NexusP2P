# 部署手册

对应 Task 3.3（coturn）与 Task 6.x（证书、自启、运行）。

> 前提（已实测确认）：**443 可用、80 被封、UDP 未被 QoS 限速**。
> 见 [`docs/spikes/network-constraints.md`](../docs/spikes/network-constraints.md)。

## 组件

| 组件 | 端口 | 作用 |
|---|---|---|
| 信令服务器（ASP.NET Core） | 443 | 配对两端、转发 SDP/ICE。**不碰文件字节** |
| coturn | 3478 (UDP/TCP)、5349 (TLS)、49160-50160 (UDP) | 打洞失败时的中继兜底 |

## 一、证书（DNS-01）

80 被封，所以 HTTP-01 不可用。TLS-ALPN-01 虽然用 443 因此技术上可行，
但它要在验证期间抢占 443、与常驻服务冲突。**DNS-01 不碰端口也不中断服务。**

```bash
# 以 acme.sh + Cloudflare 为例，其他 DNS 服务商换对应插件
export CF_Token="你的 API Token"
acme.sh --issue --dns dns_cf -d p2p.你的域名

acme.sh --install-cert -d p2p.你的域名 \
  --key-file       /etc/letsencrypt/live/p2p.你的域名/privkey.pem \
  --fullchain-file /etc/letsencrypt/live/p2p.你的域名/fullchain.pem \
  --reloadcmd      "systemctl reload nexusp2p-signaling && systemctl restart coturn"
```

acme.sh 会自己装好续期的 cron。**证书同时给信令服务器和 coturn 用**，
所以 `--reloadcmd` 里两个都要带上。

## 二、coturn

```bash
apt install coturn
cp deploy/coturn/turnserver.conf /etc/turnserver.conf
```

配置文件里有三处标了 ★ 必须改：

| 项 | 说明 |
|---|---|
| `external-ip` | 你的公网 IP。**不填让 coturn 自己探测，在 NAT 后面往往探测错** —— 结果是中继候选带着内网地址下发，对端永远连不上 |
| `static-auth-secret` | 用 `openssl rand -hex 32` 生成。**必须与信令服务器的 `Signaling:Turn:Secret` 完全一致** |
| `realm` | 你的域名 |

路由器上要转发：`3478/udp`、`3478/tcp`、`5349/tcp`、`49160-50160/udp`。

```bash
systemctl enable --now coturn
```

### 验证 coturn 真的在工作

```bash
# 用配置里的密钥现算一组凭据
SECRET="你的 static-auth-secret"
USER=$(( $(date +%s) + 3600 ))
PASS=$(echo -n "$USER" | openssl dgst -binary -sha1 -hmac "$SECRET" | base64)

turnutils_uclient -T -u "$USER" -w "$PASS" -p 3478 你的域名
```

看到 `success` 就说明凭据格式与服务端一致。
这一步能把「中继连不上」的两大原因（凭据格式错、端口没通）当场区分开。

也可以用 <https://icetest.info/> 之类的在线工具，填 `turn:你的域名:3478`
加上现算的凭据，看能否收到 `relay` 类型的候选。

## 三、信令服务器

```bash
dotnet publish src/NexusP2P.Signaling -c Release -o /opt/nexusp2p-signaling
```

`appsettings.Production.json`：

```json
{
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
  },
  "Signaling": {
    "PublicOrigin": "https://p2p.你的域名",
    "RoomGracePeriodSeconds": 60,
    "JoinAttemptsPerMinute": 20,
    "MaxRooms": 1000,
    "Turn": {
      "Urls": ["turn:p2p.你的域名:3478", "turns:p2p.你的域名:5349"],
      "Secret": "与 coturn 完全一致的那个密钥",
      "CredentialTtlSeconds": 3600
    }
  }
}
```

### 配置清单（AD-8）

| 键 | 必填 | 改错了会怎样 |
|---|---|---|
| `Signaling:PublicOrigin` | **是** | 缺了**服务直接拒绝启动**（刻意如此）。填错则生成的分享链接对方打不开，而这种错误极难从现象倒推 |
| `Signaling:Turn:Urls` | 否 | 不填就没有中继，打洞失败即连不上 |
| `Signaling:Turn:Secret` | 配了 Urls 则必填 | 与 coturn 不一致 → 「中继配了却永远连不上」，且没有有用的报错 |
| `Signaling:RoomGracePeriodSeconds` | 否 | 调到 0 会让**自动重连必然失效**（AD-7） |
| `Signaling:JoinAttemptsPerMinute` | 否 | 调太高会让九位码可被枚举 |
| `Signaling:MaxRooms` | 否 | 调太高时有人不停建房可以吃光内存 |

配置在**启动时**校验并一次报出全部问题，不会带着错配置跑起来。

### systemd

```ini
[Unit]
Description=NexusP2P 信令服务器
After=network.target

[Service]
Type=notify
WorkingDirectory=/opt/nexusp2p-signaling
ExecStart=/usr/bin/dotnet /opt/nexusp2p-signaling/NexusP2P.Signaling.dll
Environment=ASPNETCORE_ENVIRONMENT=Production
Restart=always
RestartSec=5

# 只需要绑定 443 的能力，不需要 root
AmbientCapabilities=CAP_NET_BIND_SERVICE
User=nexusp2p

[Install]
WantedBy=multi-user.target
```

```bash
systemctl enable --now nexusp2p-signaling
```

## 四、日常运维

```bash
# 服务是否活着，当前多少房间
curl -s https://p2p.你的域名/health | jq

# 日志
journalctl -u nexusp2p-signaling -f
tail -f /var/log/coturn/turnserver.log

# 改配置后重启
systemctl restart nexusp2p-signaling
```

`/health` 返回：

```json
{
  "status": "ok",
  "activeRooms": 2,
  "publicOrigin": "https://p2p.你的域名",
  "relayConfigured": true
}
```

`relayConfigured` 为 `false` 说明 TURN 没配上 —— **这是最容易漏的一项**，
因为不配也能在同局域网内正常工作，问题只在跨公网打洞失败时才暴露。

## 五、排查

| 现象 | 先看哪里 |
|---|---|
| 分享链接对方打不开 | `PublicOrigin` 是否与实际域名一致 |
| 局域网能传、跨公网连不上 | `/health` 的 `relayConfigured`；再用 `turnutils_uclient` 验凭据 |
| 中继配了还是连不上 | 密钥两边是否一致；`external-ip` 是否填了真实公网 IP；UDP 端口段是否转发 |
| 传输经常断 | `RoomGracePeriodSeconds` 是否被调小；看两侧日志的断开时刻是否对得上 |
| 大量 429 | 有人在枚举文件码，或 `JoinAttemptsPerMinute` 调得太低 |

## 六、用 CLI 验证整条链路

`src/NexusP2P.Cli` 是个可用的命令行客户端。GUI 出来之前先用它，
部署之后也用它验收 —— **它会直接打出走的是直连还是中继**。

打一个自包含的包（目标机器不需要装 .NET）：

```bash
./packaging/package.sh        # → dist/nexusp2p-win-x64.zip
```

两台机器（**必须在不同的网络下**，同一个局域网测不出打洞与中继）：

```
# A 机
nexusp2p.exe send 一个大文件.zip --signaling https://p2p.你的域名

# B 机（把 A 打出来的分享链接贴过来，要用引号包住 —— # 会被 shell 截断）
nexusp2p.exe receive "<分享链接>" --dest D:\收到 --signaling https://p2p.你的域名
```

连上时那一行就是结论：

| 打出来的字样 | 含义 |
|---|---|
| `已连接（打洞成功，公网直连）` | 最好的情况，没走服务器 |
| `已连接（经服务器中继，…）` | 打洞失败，中继在兜底 —— **说明 coturn 是通的** |
| `已连接（同局域网直连）` | 两台机器在同一个内网，这一轮测不出中继 |

想**强制**测中继路径，把 `Signaling:Turn:Urls` 保留、临时在客户端所在网络
封掉 UDP 直连即可；或者直接看 `tail -f /var/log/coturn/turnserver.log` 有没有分配记录。

传到一半把 B 机的网线拔掉再插回来，或者直接 Ctrl+C 关掉再用同一条命令重跑 ——
应该看到 `（其中 X 是上次留下的，本次续传）`。

## 七、还没验证的部分

诚实记录，免得把「代码写完了」当成「部署验证过了」：

- **中继路径一次真实数据都没跑过。** 凭据签发的算法有 16 条测试保证与
  coturn 的 `use-auth-secret` 协议一致，但「凭据对」不等于「中继通」
- **跨 NAT 打洞成功率** —— 需要两台在不同网络下的真实机器
- **中继实际吞吐** —— 取决于家庭上行带宽，需实测
- **证书自动续期** —— 需要等到第一次续期（约 60 天后）才能确认

已经验证过的（同机回环，两个独立进程）：建房、拿码、进房、
SDP 与 ICE 转发、DTLS + SCTP 数据通道、整文件与文件夹传输、
进程被杀后重开续传、断线自动重连 3 次后转手动。
