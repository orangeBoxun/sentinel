#!/usr/bin/env bash
# 时效实测：在已知时刻把 Edge 切到违规页面，量监控多久检测到。
# dry-run + 只记录动作，所以不会动窗口。
#
# 注意：靶子必须是进客盒子不会拦的页面，否则测到的是进客盒子关页面的速度，不是监控的延迟。
set -u

cd "C:/Users/czyjq/Desktop/temp/sentinel"

EXE="src/Sentinel.Cli/bin/Debug/net8.0-windows/Sentinel.Cli.exe"
MSEDGE="C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe"
TARGET_URL='https://noobdream.com/index/'
ALLOWED_URL='https://example.com'
AUDIT="$TEMP/Sentinel/logs/audit-$(date +%Y%m%d).jsonl"
LOG="$TEMP/sentinel-latency.log"

rm -f "$LOG" "$AUDIT"

INTERVAL="${1:-0}"

timeout 70 "$EXE" run --config examples/latency-test.json --interval "$INTERVAL" > "$LOG" 2>&1 &
MON=$!

sleep 4
echo "T0  $(date +%H:%M:%S.%3N)  切到靶子页"
"$MSEDGE" "$TARGET_URL" >/dev/null 2>&1
sleep 12

echo "T1  $(date +%H:%M:%S.%3N)  切到 example.com（放行）"
"$MSEDGE" "$ALLOWED_URL" >/dev/null 2>&1
sleep 12

echo "T2  $(date +%H:%M:%S.%3N)  再切回靶子页"
"$MSEDGE" "$TARGET_URL" >/dev/null 2>&1
sleep 16

kill $MON 2>/dev/null
wait $MON 2>/dev/null

echo
echo "=== 审计日志时间戳（毫秒）==="
if [ -f "$AUDIT" ]; then
  grep -o '"Timestamp":"[^"]*"' "$AUDIT" | sed 's/.*T//; s/+.*//' | cut -c1-12
else
  echo "(无)"
fi

echo
echo "=== 监控检测到的内容 ==="
grep -E "Notify|Enforce" "$LOG" | cut -c1-200

echo
echo "=== 结束时 Edge 当前页面（确认靶子没被别的软件关掉）==="
timeout 60 "$EXE" diagnose msedge 2>&1 | sed -n '2,3p' | cut -c1-160
