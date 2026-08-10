#!/usr/bin/env bash
# 打自包含包：解压即用，目标机器不需要装 .NET。
#
#   ./packaging/package.sh              # 全部
#   ./packaging/package.sh win          # 只打 Windows 客户端包
#   ./packaging/package.sh linux        # 只打 Linux 服务端包
#
# 产物：
#   dist/nexusp2p-win-x64.zip                    客户端 + 信令服务器（两台电脑对测用）
#   dist/nexusp2p-signaling-linux-x64.tar.gz     服务器上跑的信令服务器
#   dist/nexusp2p-signaling-linux-arm64.tar.gz   ARM 服务器（树莓派、部分云主机）

set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
what="${1:-all}"

# 清空一个目录，但不删除目录本身。
#
# Windows 上删目录经常失败（"Device or resource busy"）—— 杀毒软件、
# 资源管理器的预览窗格、或残留的 MSBuild 常驻进程都会持着句柄，
# 而句柄可能在目录已经空了之后还挂着一会儿。
# 我们要的只是「里面没有旧文件」，删掉目录本身没有任何必要。
clean_dir() {
  local dir="$1"
  mkdir -p "$dir"
  find "$dir" -mindepth 1 -delete 2>/dev/null || true
}

publish() {
  local project="$1" rid="$2" out="$3"
  local version_args=()
  if [[ -n "${NEXUSP2P_VERSION:-}" ]]; then
    version_args+=("-p:Version=$NEXUSP2P_VERSION")
  fi
  echo "==> 发布 $project ($rid)"
  dotnet publish "$root/src/$project" \
    -c Release \
    -r "$rid" \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true \
    -p:DebugType=none \
    "${version_args[@]}" \
    -o "$out" \
    --nologo
}

package_windows() {
  local out="$root/dist/nexusp2p-win-x64"

  # 干净重来：残留的旧文件会跟着进包，而「为什么对方那台是旧版本」很难查
  clean_dir "$out"
  rm -f "$out.zip"

  # 三个产物，各有各的用途：
  #   NexusP2P-Desktop.exe   图形界面，双击就用，**不出现命令行窗口**
  #   nexusp2p.exe           命令行版，脚本与批量场景用
  #   nexusp2p-signaling.exe 信令服务器，两台电脑对测时本地起一个
  #
  # 名字必须两两大小写不同 —— 见 NexusP2P.Desktop.csproj 里的说明。
  publish NexusP2P.Desktop   win-x64 "$out"
  publish NexusP2P.Cli       win-x64 "$out"
  publish NexusP2P.Signaling win-x64 "$out"

  cp "$root/packaging/README.md" "$out/使用说明.md"
  cp "$root/packaging/nexusp2p.json" "$out/"
  cp "$root/packaging/start-signaling.ps1" "$out/"
  cp "$root/packaging/start-signaling.cmd" "$out/"

  # web.config 只对 IIS 托管有意义，这里是控制台自托管，留着只会让人困惑。
  # appsettings.Development.json 同理：它只在 ASPNETCORE_ENVIRONMENT=Development
  # 时生效，而包里同时存在两份配置会让「我明明改了却没生效」变成常见困惑。
  rm -f "$out/web.config" \
        "$out/nexusp2p-signaling.staticwebassets.endpoints.json" \
        "$out/appsettings.Development.json"

  echo "==> 打包 win-x64"
  ( cd "$root/dist" && powershell -NoProfile -Command \
      "Compress-Archive -Path 'nexusp2p-win-x64\\*' -DestinationPath 'nexusp2p-win-x64.zip' -Force" )
}

package_linux() {
  local arch rid name out
  for arch in x64 arm64; do
    rid="linux-$arch"
    name="nexusp2p-signaling-$rid"
    out="$root/dist/$name"

    clean_dir "$out"
    rm -f "$out.tar.gz"

    publish NexusP2P.Signaling "$rid" "$out"

    # 只留一份配置文件。
    #
    # 发布产物自带 appsettings.json 与 appsettings.Development.json，
    # 再放一个 appsettings.Production.json 就有三份 —— 而它们之间是覆盖关系，
    # 取决于 ASPNETCORE_ENVIRONMENT。结果是「我明明改了配置却没生效」，
    # 且完全看不出改错了哪一份。直接盖掉 appsettings.json：它无条件被读取。
    cp "$root/packaging/appsettings.Production.json" "$out/appsettings.json"
    rm -f "$out/appsettings.Development.json"

    cp "$root/packaging/nexusp2p-signaling.service" "$out/"
    cp "$root/packaging/nginx-nexusp2p.conf" "$out/"
    cp "$root/packaging/LINUX.md" "$out/部署说明.md"
    cp "$root/packaging/1PANEL.md" "$out/1Panel部署说明.md"
    mkdir -p "$out/docker"
    cp "$root/packaging/docker/Dockerfile" "$out/docker/"
    cp "$root/packaging/docker/docker-compose.yml" "$out/docker/"

    rm -f "$out/web.config" "$out/nexusp2p-signaling.staticwebassets.endpoints.json"

    echo "==> 打包 $rid"
    #
    # 两件事：
    #
    # 一、用 tar 而不是 zip —— zip 根本不保留可执行权限。
    #
    # 二、--mode=0755 是全局的，所以连 json 也变成可执行，看着别扭。
    #     但这是在 Windows 上打包：NTFS 没有可执行位，Git Bash 的 chmod
    #     也写不进去，tar 会把二进制记成 0644 —— 解压出来直接
    #     Permission denied，而这最容易被当成「包坏了」。
    #     配置文件多个执行位没有任何实际影响，可读性上让一步换来一定能跑。
    ( cd "$root/dist" && tar -czf "$name.tar.gz" --mode=0755 "$name" )
  done
}

case "$what" in
  win)   package_windows ;;
  linux) package_linux ;;
  all)   package_windows; package_linux ;;
  *)     echo "用法：package.sh [win|linux|all]" >&2; exit 1 ;;
esac

echo
echo "产物："
ls -la "$root/dist" | grep -E "\.zip$|\.tar\.gz$"
