#!/usr/bin/env bash
# 端到端验证：正则匹配、白名单优先级、时间窗。
# 这几项此前只有单元测试覆盖，没在真实窗口上跑过。
#
# 靶子：Edge，当前停在 https://noobdream.com/index/
# 每个场景跑 10 秒，看有没有命中。
set -u

cd "C:/Users/czyjq/Desktop/temp/sentinel"

EXE="src/Sentinel.Cli/bin/Debug/net8.0-windows/Sentinel.Cli.exe"
D="$TEMP/sentinel-e2e"
mkdir -p "$D"

echo "现在时间: $(date '+%Y-%m-%d %H:%M:%S %A')"
echo "（时间窗设定：生效窗口 03:00-04:00，当前 13 点档 —— 应当不生效）"
echo

run() {
  local name="$1" cfg="$2"
  echo "── $name ──"
  local out
  out=$(timeout 12 "$EXE" run --config "$cfg" --interval 0 2>&1 | grep -E "Notify|Enforce" | head -3)
  if [ -z "$out" ]; then
    echo "   无命中"
  else
    echo "$out" | cut -c1-160
  fi
  echo
}

common_head() {
  cat <<'JSON'
{
  "Version": 1,
  "Targets": [{
    "Id": "t", "DisplayName": "规则测试", "ProcessNames": ["msedge"], "Enabled": true,
    "Reader": { "ScanUrl": true, "ScanTitle": true, "ScanBody": false, "IntervalMs": 0, "MaxBodyChars": 4096 },
    "Rules": [
JSON
}

common_tail() {
  cat <<'JSON'
    ]
  }],
  "Policies": [{ "Id": "p-log", "Name": "只记录", "Actions": ["Log"], "GraceSeconds": 0, "CooldownSeconds": 0, "DebounceCount": 1 }],
  "Schedules": [{ "Id": "off-hours", "DaysOfWeek": [], "TimeWindows": [{ "Start": "03:00", "End": "04:00" }] }],
  "Protection": { "Enabled": false, "Mode": "None", "SecretHash": null, "DelaySeconds": 0 },
  "Logging": { "LogDirectory": "%TEMP%\\Sentinel\\logs", "LogBodyText": false, "LogUrlHash": false },
  "Ui": { "Locale": "zh-CN", "DryRun": true }
}
JSON
}

# A：正则规则，但绑定了一个当前不生效的时间窗 —— 应当不命中
{
  common_head
  cat <<'JSON'
      { "Id": "r1", "Name": "正则匹配 noobdream（绑不生效时间窗）", "Enabled": true,
        "ScheduleId": "off-hours", "PolicyId": "p-log", "Priority": 10,
        "Match": { "Type": "Regex", "Fields": ["Url"], "Patterns": ["^https?://([a-z0-9-]+\\.)?noobdream\\.com/"], "CaseSensitive": false } }
JSON
  common_tail
} > "$D/a-regex-inactive-schedule.json"

# B：同一条正则规则，去掉时间窗 —— 应当命中（同时证明正则端到端可用）
{
  common_head
  cat <<'JSON'
      { "Id": "r1", "Name": "正则匹配 noobdream", "Enabled": true,
        "PolicyId": "p-log", "Priority": 10,
        "Match": { "Type": "Regex", "Fields": ["Url"], "Patterns": ["^https?://([a-z0-9-]+\\.)?noobdream\\.com/"], "CaseSensitive": false } }
JSON
  common_tail
} > "$D/b-regex.json"

# C：低优先级拦截规则 + 高优先级白名单，两者都匹配 —— 白名单应当胜出，不命中
{
  common_head
  cat <<'JSON'
      { "Id": "r-block", "Name": "关键词拦 noobdream", "Enabled": true,
        "PolicyId": "p-log", "Priority": 10,
        "Match": { "Type": "Keyword", "Fields": ["Url"], "Patterns": ["noobdream"], "CaseSensitive": false } },
      { "Id": "r-allow", "Name": "白名单放行 noobdream.com/index", "Enabled": true,
        "PolicyId": "p-log", "Priority": 100,
        "Match": { "Type": "Whitelist", "Fields": ["Url"], "Patterns": ["noobdream.com/index"], "CaseSensitive": false } }
JSON
  common_tail
} > "$D/c-whitelist.json"

# D：只有拦截规则（白名单关掉）—— 应当命中。用来证明 C 的"无命中"确实是白名单造成的
{
  common_head
  cat <<'JSON'
      { "Id": "r-block", "Name": "关键词拦 noobdream", "Enabled": true,
        "PolicyId": "p-log", "Priority": 10,
        "Match": { "Type": "Keyword", "Fields": ["Url"], "Patterns": ["noobdream"], "CaseSensitive": false } }
JSON
  common_tail
} > "$D/d-block-only.json"

echo "预期：A 无命中（时间窗挡住）／B 命中（正则有效）／C 无命中（白名单胜出）／D 命中（证明 C 确实是白名单的功劳）"
echo

run "A. 正则 + 不生效的时间窗（预期：无命中）" "$D/a-regex-inactive-schedule.json"
run "B. 正则，无时间窗（预期：命中）" "$D/b-regex.json"
run "C. 拦截 + 更高优先级白名单（预期：无命中）" "$D/c-whitelist.json"
run "D. 只有拦截规则（预期：命中）" "$D/d-block-only.json"

echo "结束时 Edge 页面："
timeout 60 "$EXE" diagnose msedge 2>&1 | sed -n '2,3p' | cut -c1-140
