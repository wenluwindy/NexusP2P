#!/usr/bin/env bash
# 打依赖框架的 DLL 包：需要服务器上有 .NET 9 运行时，包体积小很多。
#
#   ./packaging/package-dll.sh              # 全部
#   ./packaging/package-dll.sh linux        # 只打 Linux 服务端包
#
# 产物：
#   dist/nexusp2p-signaling-linux-x64-dll.tar.gz
#   dist/nexusp2p-signaling-linux-arm64-dll.tar.gz

set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
what="${1:-all}"

# 清空目录但不删除目录本身。Windows 上删目录经常因为残留句柄失败
# （"Device or resource busy"）—— 与 package.sh 里同一个函数、同一个理由。
clean_dir() {
  local dir="$1"
  mkdir -p "$dir"
  find "$dir" -mindepth 1 -delete 2>/dev/null || true
}

publish() {
  local project="$1" rid="$2" out="$3"
  echo "==> 发布 $project ($rid, 依赖框架)"
  dotnet publish "$root/src/$project" \
    -c Release \
    -r "$rid" \
    --self-contained false \
    -p:DebugType=none \
    -o "$out" \
    --nologo
}

package_linux() {
  local arch rid name out
  for arch in x64 arm64; do
    rid="linux-$arch"
    name="nexusp2p-signaling-$rid-dll"
    out="$root/dist/$name"

    clean_dir "$out"
    rm -f "$out.tar.gz"

    publish NexusP2P.Signaling "$rid" "$out"

    # 只留一份配置文件
    cp "$root/packaging/appsettings.Production.json" "$out/appsettings.json"
    rm -f "$out/appsettings.Development.json"

    # systemd 单元文件需要改启动命令
    sed 's|ExecStart=.*|ExecStart=/usr/bin/dotnet /opt/nexusp2p-signaling/nexusp2p-signaling.dll --urls http://0.0.0.0:5000|' \
      "$root/packaging/nexusp2p-signaling.service" > "$out/nexusp2p-signaling.service"

    cp "$root/packaging/nginx-nexusp2p.conf" "$out/"

    # 写一个简化的部署说明
    cat > "$out/部署说明.md" <<'EOF'
# DLL 包部署说明 (V2.0.0)

## 前提：服务器上需要 .NET 9 运行时

```bash
# Debian/Ubuntu
wget https://dot.net/v1/dotnet-install.sh
bash dotnet-install.sh --channel 9.0 --runtime aspnetcore --install-dir /usr/share/dotnet
ln -sf /usr/share/dotnet/dotnet /usr/bin/dotnet

# 或者用发行版包管理器，见 https://learn.microsoft.com/zh-cn/dotnet/core/install/linux
```

## 部署步骤

```bash
# 1. 上传并解压
cd /opt
tar -xzf nexusp2p-signaling-linux-x64-dll.tar.gz
mv nexusp2p-signaling-linux-x64-dll nexusp2p-signaling

# 2. 改配置（V2.0.0 必改项）
vi /opt/nexusp2p-signaling/appsettings.json

# ★ 必改：PublicOrigin（对外公开的地址）
"PublicOrigin": "https://p2p.你的域名"

# ★ 必改：BehindReverseProxy（如果用 nginx/Caddy）
"BehindReverseProxy": true

# ★ V2.0.0 新增：EnableJoinRateLimit
# 生产环境建议 true，内网可设为 false
"EnableJoinRateLimit": true

# 3. 创建用户并安装服务
useradd --system --no-create-home --shell /usr/sbin/nologin nexusp2p
mkdir -p /var/lib/nexusp2p-signaling
chown -R nexusp2p:nexusp2p /var/lib/nexusp2p-signaling

cp /opt/nexusp2p-signaling/nexusp2p-signaling.service /etc/systemd/system/
systemctl daemon-reload
systemctl enable --now nexusp2p-signaling
systemctl status nexusp2p-signaling

# 4. 验证
curl -s http://localhost:5000/health
```

## V2.0.0 新特性

### 1. STUN 服务器本地化（中国区优化）
默认配置了国内可访问的 STUN 服务器：
- 小米 STUN: stun.miwifi.com
- 哔哩哔哩 STUN: stun.chat.bilibili.com
- 湖南卫视 STUN: stun.hitv.com

**无需修改**，开箱即用。

### 2. 速率限制可选化
新增 `EnableJoinRateLimit` 开关：
- 生产环境：建议 `true`
- 内网环境：可设为 `false`

### 3. 一对多传输支持
支持一个发送方对多个接收方同时传输。

## 用 1Panel 运行环境

如果用 1Panel 的运行环境而不是 systemd：

1. 在「运行环境」里安装 .NET 9
2. 在「网站」里创建一个反向代理站点（见 nginx-nexusp2p.conf）
3. 启动命令：`dotnet /opt/nexusp2p-signaling/nexusp2p-signaling.dll --urls http://0.0.0.0:5000`
4. 环境变量根据需要设置（不是必须）

完整的 nginx 配置和注意事项见随包的 `nginx-nexusp2p.conf`。

## 故障排查

### 连接失败：等待数据通道打开超时
**原因**: STUN 服务器不可达或防火墙阻止 UDP
**解决**: 
1. 检查防火墙是否允许 UDP 端口
2. 测试 STUN 服务器连通性
3. 如在海外，可改用 Google STUN

### 429 Too Many Requests
**原因**: 触发速率限制
**解决**: 
- 内网环境：设置 `"EnableJoinRateLimit": false`
- 公网环境：增加 `JoinAttemptsPerMinute` 值

### 更多帮助
查看项目文档或访问 https://github.com/your-repo
EOF

    rm -f "$out/web.config" "$out/nexusp2p-signaling.staticwebassets.endpoints.json"

    echo "==> 打包 $rid"
    ( cd "$root/dist" && tar -czf "$name.tar.gz" "$name" )
  done
}

case "$what" in
  linux) package_linux ;;
  all)   package_linux ;;
  *)     echo "用法：package-dll.sh [linux|all]" >&2; exit 1 ;;
esac

echo
echo "产物："
ls -lh "$root/dist" | grep -E "dll\.tar\.gz$"
