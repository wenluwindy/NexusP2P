# 在 1Panel 上部署信令服务器

## 先说结论：不需要用 1Panel 的「运行环境」

「运行环境」是给**需要语言运行时**的应用准备的（PHP、Node、Java…）。
我们这个二进制是**自包含**的 —— .NET 运行时已经打在里面，
服务器上装不装 .NET 都一样。所以走「运行环境」只会多绕一层。

两条路，按你的情况选：

| | 适合 | 复杂度 |
|---|---|---|
| **方案 A：宿主机 systemd + 1Panel 反向代理** | 大多数情况，尤其是你已经有网站占着 443 | 低 |
| **方案 B：1Panel 容器编排** | 想让面板统一管进程、统一看日志 | 中 |

两条路都要在 1Panel 里配一个**反向代理网站**来终结 HTTPS，
因为你的 443 已经被现有网站占着，我们不去抢它。

> 1Panel 各版本的菜单名可能略有差别，下面用的是常见叫法。
> 但**验证步骤是精确的** —— 照着第四节核对就能确认到底通没通。

---

## 一、上传并解压

1Panel 的「文件」里把 `nexusp2p-signaling-linux-x64.tar.gz` 传到
`/opt`，然后在「终端」里：

```bash
cd /opt
tar -xzf nexusp2p-signaling-linux-x64.tar.gz
mv nexusp2p-signaling-linux-x64 nexusp2p-signaling
chmod +x /opt/nexusp2p-signaling/nexusp2p-signaling
mkdir -p /var/lib/nexusp2p-signaling
```

> ARM 服务器（部分云主机、树莓派）用 `-linux-arm64` 那个包。
> 拿不准就 `uname -m`：`x86_64` → x64，`aarch64` → arm64。

先手动跑一次，确认这个二进制在你的系统上能起来：

```bash
cd /opt/nexusp2p-signaling
Signaling__PublicOrigin=https://p2p.你的域名 ./nexusp2p-signaling --urls http://0.0.0.0:5000
```

看到 `Now listening on: http://0.0.0.0:5000` 就说明二进制没问题。
Ctrl+C 停掉，继续往下。

> **起不来的话先看这一步的报错。** 配置在启动时校验并一次报出全部问题，
> 所以错误信息就是原因本身，不用猜。

## 二、改配置

编辑 `/opt/nexusp2p-signaling/appsettings.json`：

```jsonc
{
  "Signaling": {
    "PublicOrigin": "https://p2p.你的域名",   // ★ 对外地址，填错则分享链接对方打不开
    "BehindReverseProxy": true,               // ★ 走 1Panel 的反向代理，必须 true
    "KnownProxies": []                        // 见下面「最容易踩的一步」
  }
}
```

### 最容易踩的一步：`KnownProxies`

**1Panel 的 OpenResty 跑在 Docker 容器里。** 于是请求到达我们的服务时，
源地址不是 `127.0.0.1`，而是 Docker 网桥地址（常见是 `172.17.0.1`，
也可能是 `172.18.x.1` 之类）。

`KnownProxies` 留空表示「只信任本机」—— 那样转发头不会被采信，
**入房限速就退化成所有人共用一个配额**：几十次尝试之后全体被 429，
而真想枚举九位文件码的人也一样不受限。

**不用猜。** 服务跑起来、通过网站访问一次之后，日志里会直接把该填的地址告诉你：

```
warn: 收到 X-Forwarded-For（1.2.3.4）但没有采信：请求来自 172.17.0.1，
      不在可信代理范围内。……把 Signaling:KnownProxies 设成 ["172.17.0.1"] 即可
```

照抄那个 IP 填进去，重启，警告消失即为配好：

```jsonc
"KnownProxies": ["172.17.0.1"]
```

> 反过来，如果日志里出现的是「BehindReverseProxy 是 false，已忽略」，
> 说明第二节那一项忘了改。

### 填了 TURN 密钥之后

```bash
chown nexusp2p:nexusp2p /opt/nexusp2p-signaling/appsettings.json
chmod 600 /opt/nexusp2p-signaling/appsettings.json
```

密钥泄露的后果是任何人都能白嫖你家的中继带宽。

---

## 方案 A：宿主机 systemd

```bash
useradd --system --no-create-home --shell /usr/sbin/nologin nexusp2p
chown -R nexusp2p:nexusp2p /var/lib/nexusp2p-signaling

cp /opt/nexusp2p-signaling/nexusp2p-signaling.service /etc/systemd/system/
```

**改一处**：把单元里的 `--urls http://127.0.0.1:5000` 换成
`--urls http://0.0.0.0:5000`。

原因：OpenResty 在容器里，它连过来用的是网桥地址而不是本机回环 ——
只绑 `127.0.0.1` 的话容器根本连不上，表现为反向代理 502。

> 绑 `0.0.0.0` 意味着 5000 端口对外也开着。**去 1Panel 的
> 「主机 → 防火墙」确认 5000 没有放行**（默认不放行）。
> 放行了的话，别人可以绕过 HTTPS 直连你的信令服务器 ——
> 不至于泄露文件（服务器本来就不碰文件字节），但限速与 TLS 都白配了。

```bash
systemctl daemon-reload
systemctl enable --now nexusp2p-signaling
systemctl status nexusp2p-signaling
```

## 方案 B：1Panel 容器编排

用 `docker/` 目录下的两个文件。先把二进制放到宿主机同一个位置
（照第一节做），然后在 1Panel「容器 → 编排 → 创建编排」里粘贴
`docker-compose.yml` 的内容。

它用 bind mount 挂载宿主机上的二进制与配置，所以**改配置不用重建镜像**，
`docker compose restart` 就生效。

镜像基于 `mcr.microsoft.com/dotnet/runtime-deps` —— 这个镜像的用途正是
承载自包含的 .NET 程序，native 依赖都齐。

容器里的服务监听 5000 并加入 1Panel 的网络，所以反向代理的目标可以直接写
容器名。这条路的好处是不用操心「绑 0.0.0.0 还是 127.0.0.1」。

---

## 三、在 1Panel 里配反向代理网站

「网站 → 创建网站 → **反向代理**」：

| 填什么 | 值 |
|---|---|
| 主域名 | `p2p.你的域名` |
| 代理地址 | 方案 A：`http://172.17.0.1:5000`；方案 B：`http://nexusp2p-signaling:5000` |

> 代理地址填 `127.0.0.1:5000` 大概率不通 —— OpenResty 在容器里，
> 那个回环是容器自己的。**502 就是这个原因**。
> 网桥地址用 `ip addr show docker0` 查（一般是 `172.17.0.1`）。

创建完先去「证书」里给这个域名签一张证书，然后在网站的 HTTPS 里启用。

> **80 端口被封，所以必须用 DNS 验证签发，不能用 HTTP 验证。**
> 1Panel 的「证书 → DNS 账号」里配好你的 DNS 服务商 API，
> 申请时选 DNS 方式。

### 必须补三行配置

这是**整个部署最关键的一步**。在这个网站的「配置文件」里找到 `location /`，
确认有这三样东西（1Panel 生成的反向代理默认可能不全）：

```nginx
location / {
    proxy_pass http://172.17.0.1:5000;
    proxy_http_version 1.1;

    # ① WebSocket 升级 —— 少了它信令完全不通
    proxy_set_header Upgrade    $http_upgrade;
    proxy_set_header Connection "upgrade";

    # ② 真实客户端 IP —— 入房限速要靠它
    proxy_set_header Host            $host;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;

    # ③ 长超时 —— 默认 60 秒会把信令连接掐掉
    proxy_read_timeout 7d;
    proxy_send_timeout 7d;
    proxy_buffering off;
}
```

三处各自漏掉会怎样：

| 漏掉 | 现象 |
|---|---|
| ① `Upgrade` / `Connection` | **`/health` 一切正常，但建房就失败** —— 最容易误判成程序 bug |
| ② `X-Forwarded-For` | 能传文件，但入房限速失效（悄悄的，没有任何现象） |
| ③ `proxy_read_timeout` | 传大文件传到一分多钟时信令断开，自动重连也落不到原房间 |

改完 `nginx -t && systemctl reload openresty`，或者用 1Panel 界面上的重载。

---

## 四、验证（这一节是精确的，照着核）

### 1. HTTP 通不通

```bash
curl -s https://p2p.你的域名/health
```

期望：

```json
{
  "status": "ok",
  "activeRooms": 0,
  "publicOrigin": "https://p2p.你的域名",
  "relayConfigured": false,
  "behindReverseProxy": true
}
```

四个字段逐个核：

| 字段 | 不对说明 |
|---|---|
| `publicOrigin` | 与实际域名不符 → 分享链接对方打不开 |
| `behindReverseProxy` | 是 `false` → 第二节那一项没改 |
| `relayConfigured` | 是 `false` → 没配 TURN，跨公网打洞失败就连不上 |
| `activeRooms` | 下一步建房后应变成 1 |

502 的话就是代理地址不对（见第三节的提示）。

### 2. 限速有没有按真实 IP 算

```bash
journalctl -u nexusp2p-signaling --since "5 minutes ago" | grep -i forwarded
```

**没有任何输出 = 配对了。** 有 warn 输出的话，那行日志会直接告诉你
该把哪个 IP 填进 `KnownProxies`。

### 3. WebSocket 到底通没通

`/health` 是普通 HTTP，**它正常不代表 WebSocket 通**。
拿客户端连一次才算：

```
nexusp2p.exe send 随便一个文件.zip --signaling https://p2p.你的域名
```

能打出九位文件码 = WebSocket 建房成功。打不出来 → 回去看第三节的 ①。

同时另开一个终端看：

```bash
curl -s https://p2p.你的域名/health
```

`activeRooms` 应该变成 `1`。

### 4. 真的传一次

两台不同网络下的电脑，一台 send 一台 receive。连上时那行括号里：

| 括号里 | 含义 |
|---|---|
| `打洞成功，公网直连` | 最好，没经过你的服务器 |
| `经服务器中继，…` | 打洞失败但中继兜住了，说明 coturn 也通了 |

---

## 五、日常

```bash
systemctl restart nexusp2p-signaling      # 改配置后
journalctl -u nexusp2p-signaling -f       # 看日志
```

方案 B 用 1Panel 的容器界面看日志、重启。

配置在**启动时**校验并一次报出全部问题，所以改完起不来时，
`journalctl` 里那一段就是原因，不用猜。

## 六、还没验证的部分

诚实记录：

- **这份 Linux 包我只做了构建与 ELF 校验，没有在 Linux 上跑过**
  （开发机上没有 WSL 也没有 Docker）。代码是纯 ASP.NET Core、
  没有 Windows 专有 API，自包含单文件也不依赖系统 .NET —— 但
  「构建成功」不等于「跑起来了」。所以第一节特意让你**先手动跑一次**。
- **1Panel 的菜单名与生成的 nginx 配置随版本变化**，上面写的是常见形态。
  第四节的验证步骤不受此影响，以它为准。
- **中继（coturn）一次真实数据都没跑过。** coturn 本身不在这个包里，
  见仓库 `deploy/README.md`。
