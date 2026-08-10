#!/usr/bin/env bash
# 跑一组 SCTP 节拍周期的对照实验，验证吞吐是否由库内部节拍决定。
# 用法： bash run_matrix.sh
set -u

cd "$(dirname "$0")"
export PYTHONUTF8=1
export PYTHONIOENCODING=utf-8

OUT=./results
mkdir -p "$OUT"

# 「节拍毫秒 传输量MiB 端口」—— 慢的配置少传点，免得等太久
CONFIGS=(
  "0   16  5091"
  "5   64  5092"
  "1  128  5093"
)

for cfg in "${CONFIGS[@]}"; do
  read -r burst size port <<<"$cfg"
  tag="burst${burst}"
  log="$OUT/server-$tag.log"

  echo ""
  echo "############ 节拍=${burst}ms（0 表示库默认 50ms） 传输=${size}MiB ############"

  dotnet run --no-launch-profile -c Release --no-build -- \
    --size-mb "$size" --burst-ms "$burst" --port "$port" >"$log" 2>&1 &
  server_pid=$!

  # 等服务端起来
  for _ in $(seq 1 40); do
    grep -q "Now listening on" "$log" 2>/dev/null && break
    sleep 0.5
  done

  python drive_receiver.py --port "$port" --timeout 400 \
    --shot "$OUT/shot-$tag.png" >"$OUT/browser-$tag.log" 2>&1

  # 给服务端一点时间打完结果
  sleep 3
  kill "$server_pid" 2>/dev/null
  wait "$server_pid" 2>/dev/null

  echo "--- 服务端 ---"
  grep -vE "Microsoft.Hosting|Content root|Hosting environment|请用 Chrome|localhost 属于" "$log" | tail -25
done

echo ""
echo "全部完成，详细日志在 $OUT/"
