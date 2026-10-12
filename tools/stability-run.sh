#!/usr/bin/env bash
# 长时间稳定性跑测：连续读取一个常驻应用窗口，每 60 秒采样内存/CPU。
#
# 配置要点：规则永不命中（匹配一个不存在的字符串），所以只有"读取"这一条路径被压测，
# 不会触发任何动作；再加 DryRun 双保险。
#
# 用法: stability-run.sh [分钟数]
set -u

cd "C:/Users/czyjq/Desktop/temp/sentinel"

MINUTES="${1:-45}"
EXE="src/Sentinel.Cli/bin/Debug/net8.0-windows/Sentinel.Cli.exe"
D="$TEMP/sentinel-stability"
mkdir -p "$D"
OUT="$D/run.log"
MONITOR_LOG="$D/monitor.log"

rm -f "$OUT" "$MONITOR_LOG"

cat > "$D/config.json" <<'JSON'
{
  "Version": 1,
  "Targets": [
    { "Id": "weixin", "DisplayName": "微信（常驻靶子）", "ProcessNames": ["Weixin"], "Enabled": true,
      "Reader": { "ScanUrl": true, "ScanTitle": true, "ScanBody": true, "IntervalMs": 1000, "MaxBodyChars": 4096 },
      "Rules": [{ "Id": "r-none", "Name": "永不命中", "Enabled": true, "PolicyId": "p-none", "Priority": 1,
        "Match": { "Type": "Keyword", "Fields": ["Url"], "Patterns": ["zzz-never-matches-zzz"], "CaseSensitive": false } }] },
    { "Id": "claude", "DisplayName": "Claude（常驻靶子）", "ProcessNames": ["claude"], "Enabled": true,
      "Reader": { "ScanUrl": true, "ScanTitle": true, "ScanBody": true, "IntervalMs": 1000, "MaxBodyChars": 4096 },
      "Rules": [{ "Id": "r-none", "Name": "永不命中", "Enabled": true, "PolicyId": "p-none", "Priority": 1,
        "Match": { "Type": "Keyword", "Fields": ["Url"], "Patterns": ["zzz-never-matches-zzz"], "CaseSensitive": false } }] }
  ],
  "Policies": [{ "Id": "p-none", "Name": "绝不动作", "Actions": [], "GraceSeconds": 0, "CooldownSeconds": 0, "DebounceCount": 1 }],
  "Schedules": [],
  "Protection": { "Enabled": false, "Mode": "None", "SecretHash": null, "DelaySeconds": 0 },
  "Logging": { "LogDirectory": "%TEMP%\\Sentinel\\stability-logs", "LogBodyText": false, "LogUrlHash": false },
  "Ui": { "Locale": "zh-CN", "DryRun": true }
}
JSON

sample() {
  powershell.exe -NoProfile -Command "
    \$p = Get-Process Sentinel.Cli -ErrorAction SilentlyContinue | Sort-Object StartTime -Descending | Select-Object -First 1
    if (\$p) { '{0,8:N0} MB   CPU累计 {1,7:N1}s   线程 {2}' -f (\$p.WorkingSet64/1MB), \$p.CPU, \$p.Threads.Count }
    else { '         (监控进程未运行)' }
  " 2>/dev/null | tr -d '\r'
}

echo "=== Sentinel 稳定性跑测 ===" | tee "$OUT"
echo "开始: $(date '+%Y-%m-%d %H:%M:%S')   计划时长: ${MINUTES} 分钟" | tee -a "$OUT"
echo | tee -a "$OUT"

timeout $((MINUTES * 60 + 30)) "$EXE" run --config "$D/config.json" --interval 200 > "$MONITOR_LOG" 2>&1 &
MON=$!

sleep 5
echo "监控进程已启动，配置在 $D/config.json" | tee -a "$OUT"
echo | tee -a "$OUT"

for ((i = 0; i <= MINUTES; i++)); do
  printf "%s  %s\n" "$(date '+%H:%M:%S')" "$(sample)" | tee -a "$OUT"
  if [ "$i" -lt "$MINUTES" ]; then sleep 60; fi
done

kill $MON 2>/dev/null
wait $MON 2>/dev/null

echo | tee -a "$OUT"
echo "=== 监控输出统计 ===" | tee -a "$OUT"
echo "输出行数: $(wc -l < "$MONITOR_LOG")" | tee -a "$OUT"
echo "读取失败次数: $(grep -c 'read-failed' "$MONITOR_LOG" 2>/dev/null || true)" | tee -a "$OUT"
echo "动作触发次数: $(grep -cE 'Notify|Enforce' "$MONITOR_LOG" 2>/dev/null || true)" | tee -a "$OUT"

AUDIT="$TEMP/Sentinel/stability-logs/audit-$(date +%Y%m%d).jsonl"
if [ -f "$AUDIT" ]; then
  echo "审计条目: $(wc -l < "$AUDIT")" | tee -a "$OUT"
  echo "其中读取失败: $(grep -c 'read-failed' "$AUDIT" 2>/dev/null || true)" | tee -a "$OUT"
fi

echo | tee -a "$OUT"
echo "跑测结束: $(date '+%Y-%m-%d %H:%M:%S')" | tee -a "$OUT"
