# 承载自包含的信令服务器。
#
# 基础镜像用 runtime-deps 而不是 aspnet：我们的二进制已经把 .NET 打在里面了，
# runtime-deps 只提供 native 依赖（libstdc++、zlib 等），正是为这种情况准备的。
# 用 aspnet 会白装一份运行时，用 debian:slim 则可能缺 native 依赖。
#
# 注意架构：linux-x64 的二进制要 amd64 的镜像，linux-arm64 要 arm64。
FROM mcr.microsoft.com/dotnet/runtime-deps:9.0

WORKDIR /app

# 二进制与配置都用挂载进来的（见 docker-compose.yml）——
# 这样改配置不用重建镜像，也不会把 TURN 密钥烤进镜像层里。

# 单文件包每次启动要把内容解到临时目录。指定一个固定位置并挂成卷，
# 免得每次重启都重新解一遍。
ENV DOTNET_BUNDLE_EXTRACT_BASE_DIR=/var/lib/nexusp2p/bundle

# curl 是给 healthcheck 用的。runtime-deps 里没有任何 HTTP 客户端，
# 而一个永远返回成功的假 healthcheck 比没有更糟 ——
# 面板上显示「健康」而服务其实已经起不来了。
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*

# 不用 root 跑。UID 写死是为了让挂载进来的文件权限可预期。
RUN useradd --system --uid 10001 --no-create-home --shell /usr/sbin/nologin nexusp2p \
 && mkdir -p /var/lib/nexusp2p/bundle \
 && chown -R nexusp2p:nexusp2p /var/lib/nexusp2p

USER nexusp2p

EXPOSE 5000

# 容器里必须绑 0.0.0.0：绑 127.0.0.1 的话反向代理连不进来（那是容器自己的回环）
ENTRYPOINT ["/app/nexusp2p-signaling", "--urls", "http://0.0.0.0:5000"]
