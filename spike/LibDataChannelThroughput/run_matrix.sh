#!/usr/bin/env bash
# 找出 75 MiB/s 到底是库的上限，还是我的背压轮询循环的上限。
#
# 关键怀疑：stall 循环用 Task.Delay(1)，而 Windows 的定时器精度是 15.6ms。
# 若抬高水位后吞吐上升，说明限速来自测量循环而不是 libdatachannel。
set -u
cd "$(dirname "$0")"
export PYTHONUTF8=1
export PYTHONIOENCODING=utf-8

OUT=./results
mkdir -p "$OUT"
DRIVER=../SipSorceryThroughput/drive_receiver.py

# 标签 分片KiB 水位MiB SCTP发送缓冲KiB 端口
CONFIGS=(
  "A-基线            64    8     0  5201"
  "B-高水位          64   32     0  5202"
  "C-大分片         256   32     0  5203"
  "D-大分片+发送缓冲 256   32  4096  5204"
)

for cfg in "${CONFIGS[@]}"; do
  read -r label chunk water sctp port <<<"$cfg"
  log="$OUT/matrix-$label.log"

  taskkill //F //IM LibDataChannelThroughput.exe >/dev/null 2>&1
  sleep 1

  dotnet run --no-launch-profile -c Release --no-build -- \
    --size-mb 512 --chunk-kb "$chunk" --high-water-mb "$water" \
    --sctp-send-kb "$sctp" --port "$port" >"$log" 2>&1 &
  pid=$!

  for _ in $(seq 1 40); do
    grep -q "请用 Chrome" "$log" 2>/dev/null && break
    sleep 0.5
  done

  python "$DRIVER" --port "$port" --timeout 240 --shot "$OUT/shot-$label.png" >/dev/null 2>&1
  sleep 3
  kill "$pid" 2>/dev/null
  wait "$pid" 2>/dev/null

  throughput=$(grep -oE "\.NET 侧吞吐     : [0-9.]+" "$log" | grep -oE "[0-9.]+$")
  stall=$(grep -oE "占 [0-9.]+%" "$log" | head -1)
  peak=$(grep -oE "工作集峰值      : [0-9.,]+" "$log" | grep -oE "[0-9.,]+$")

  printf "%-20s 分片%4s KiB  水位%3s MiB  吞吐 %6s MiB/s  背压%8s  工作集 %s MiB\n" \
    "$label" "$chunk" "$water" "${throughput:-失败}" "${stall:-?}" "${peak:-?}"
done

echo ""
echo "详细日志在 $OUT/"
