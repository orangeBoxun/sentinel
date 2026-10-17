using System.Text.RegularExpressions;
using Sentinel.Core.Models;

namespace Sentinel.Core.Config;

public enum IssueSeverity
{
    /// <summary>Config is unusable or a rule can never do what it says.</summary>
    Error,

    /// <summary>Config works, but probably not what the user intended.</summary>
    Warning,
}

public sealed record ConfigIssue(IssueSeverity Severity, string Where, string Message)
{
    public override string ToString() => $"[{Severity}] {Where}: {Message}";
}

/// <summary>
/// Checks a config for the mistakes that fail silently at runtime. Two of these bit this project
/// during development: a rule pointing at a policy that does not exist (the monitor simply logs
/// "no policy bound" and never acts), and a rule matching Body while the target never scans it.
/// </summary>
public static class ConfigValidator
{
    public static IReadOnlyList<ConfigIssue> Validate(AppConfig config)
    {
        var issues = new List<ConfigIssue>();

        var policyIds = new HashSet<string>(StringComparer.Ordinal);
        var scheduleIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var policy in config.Policies)
        {
            if (string.IsNullOrWhiteSpace(policy.Id))
                issues.Add(Error("Policies", "policy with no Id"));
            else if (!policyIds.Add(policy.Id))
                issues.Add(Error($"Policies[{policy.Id}]", "duplicate policy Id"));

            if (policy.Actions.Count == 0)
                issues.Add(Warning($"Policies[{policy.Id}]", "no actions — hits will be logged but nothing will happen"));

            if (policy.RepeatSeconds is > 0 and < 30)
                issues.Add(Warning($"Policies[{policy.Id}]", $"repeat interval is {policy.RepeatSeconds}s — that will notify this often for as long as the page stays in front"));

            if (policy.RepeatSeconds > 0 && !Engine.PolicyKind.IsReminderOnly(policy))
                issues.Add(Warning($"Policies[{policy.Id}]", "repeat interval is set but the policy takes the window away — it has no effect there"));

            if (policy.AlertStyle != AlertStyle.Balloon && !policy.Actions.Contains(ActionType.Notify))
                issues.Add(Warning($"Policies[{policy.Id}]", "alert style is set but the policy never notifies — it has no effect there"));

            if (policy.AlertStyle == AlertStyle.Fullscreen && policy.GraceSeconds > 0
                && policy.Actions.Any(a => a is ActionType.Minimize or ActionType.Close or ActionType.Kill))
            {
                issues.Add(Warning($"Policies[{policy.Id}]",
                    "full-screen alerts with a grace period will cover the screen twice: once before the action and once after"));
            }
        }

        foreach (var schedule in config.Schedules)
        {
            if (string.IsNullOrWhiteSpace(schedule.Id))
                issues.Add(Error("Schedules", "schedule with no Id"));
            else if (!scheduleIds.Add(schedule.Id))
                issues.Add(Error($"Schedules[{schedule.Id}]", "duplicate schedule Id"));

            foreach (var window in schedule.TimeWindows)
            {
                if (!IsValidTime(window.Start) || !IsValidTime(window.End))
                    issues.Add(Error($"Schedules[{schedule.Id}]", $"invalid time window '{window.Start}-{window.End}' (expected HH:mm)"));
            }
        }

        // ------------------------------------------------------------------ 大模型服务商

        var modelIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var provider in config.Models)
        {
            var where = string.IsNullOrWhiteSpace(provider.Id) ? "Models" : $"Models[{provider.Id}]";

            if (string.IsNullOrWhiteSpace(provider.Id))
                issues.Add(Error(where, "服务商没有 Id"));
            else if (!modelIds.Add(provider.Id))
                issues.Add(Error(where, "服务商 Id 重复"));

            // 没填完的服务商是 **Warning**，不是 Error。
            //
            // 以前这里是 Error，代价很重：保存时只要有一个 Error 就整次拒绝，于是一个
            // 半成品服务商会让**所有编辑都存不下去** —— 加不了规则，连把它自己删掉都做不到
            // （删除也要保存，而保存时它还在）。一个"还没填完"的中间状态把整个配置锁死了。
            //
            // 它本来也没有破坏性：没填完的服务商只是用不了，而模型规则查不到服务商时会放行
            // —— 这正是"监督没配好"该有的样子，不是配置错误。
            if (string.IsNullOrWhiteSpace(provider.Endpoint))
            {
                issues.Add(Warning(where, "还没有填请求地址 —— 这个服务商用不了"));
            }
            else if (!Uri.TryCreate(provider.Endpoint, UriKind.Absolute, out var uri)
                     || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                // 地址是完整 URL，所以这里能当场说清楚 —— 而不是等到调用时收一个
                // "请求地址不对"，那时候还得回来猜是哪里不对。
                issues.Add(Error(where, $"请求地址要是一个完整的 http/https URL：{provider.Endpoint}"));
            }

            if (string.IsNullOrWhiteSpace(provider.Model))
                issues.Add(Warning(where, "还没有填模型名 —— 这个服务商用不了"));

            // 两个可选地址：不填没事，填了就得是能用的 URL。
            foreach (var (label, value) in new[]
                     {
                         ("模型列表地址", provider.ModelListEndpoint),
                         ("官网链接", provider.Website),
                     })
            {
                if (string.IsNullOrWhiteSpace(value))
                    continue;

                if (!Uri.TryCreate(value, UriKind.Absolute, out var optional)
                    || (optional.Scheme != Uri.UriSchemeHttp && optional.Scheme != Uri.UriSchemeHttps))
                {
                    issues.Add(Warning(where, $"{label}不是一个完整的 http/https URL：{value}"));
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(config.ActiveModelId) && !modelIds.Contains(config.ActiveModelId))
            issues.Add(Error("ActiveModelId", $"指向了一个不存在的服务商：{config.ActiveModelId}"));

        // 不是错误：先写规则、再配服务商，这个顺序很自然。但不提醒的话，那条规则会
        // **什么都不做**，而界面上看不出任何区别 —— 和"配好了但没命中"长得一模一样。
        if (string.IsNullOrWhiteSpace(config.ActiveModelId)
            && config.Targets.Any(t => t.Rules.Any(r => r.Match.Type == MatchKind.Model)))
        {
            issues.Add(Warning("Models",
                "有「模型监督」的规则，但没有启用任何模型服务商 —— 这些规则现在不会生效"));
        }

        var targetIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var target in config.Targets)
        {
            var where = string.IsNullOrWhiteSpace(target.Id) ? "Targets" : $"Targets[{target.Id}]";

            if (string.IsNullOrWhiteSpace(target.Id))
                issues.Add(Error(where, "target with no Id"));
            else if (!targetIds.Add(target.Id))
                issues.Add(Error(where, "duplicate target Id"));

            if (target.ProcessNames.Count == 0 && string.IsNullOrWhiteSpace(target.WindowTitlePattern))
                issues.Add(Error(where, "no ProcessNames and no WindowTitlePattern — this target matches every window"));

            if (!string.IsNullOrWhiteSpace(target.WindowTitlePattern) && !IsValidRegex(target.WindowTitlePattern))
                issues.Add(Error(where, $"WindowTitlePattern is not a valid regex: {target.WindowTitlePattern}"));

            if (target.Reader.IntervalMs < 0)
                issues.Add(Error(where, "Reader.IntervalMs is negative"));

            var ruleIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (var rule in target.Rules)
            {
                var ruleWhere = $"{where}.Rules[{rule.Id}]";

                if (string.IsNullOrWhiteSpace(rule.Id))
                    issues.Add(Error(where, "rule with no Id"));
                else if (!ruleIds.Add(rule.Id))
                    issues.Add(Error(ruleWhere, "duplicate rule Id within this target"));

                if (rule.Pause.Enabled)
                {
                    foreach (var (label, budget) in new[]
                             {
                                 ("工作日", rule.Pause.Workday),
                                 ("周末", rule.Pause.Weekend),
                             })
                    {
                        if (budget.Minutes <= 0)
                            issues.Add(Error(ruleWhere, $"{label}的暂停时长必须大于 0 分钟"));
                        else if (budget.Minutes > 24 * 60)
                            issues.Add(Error(ruleWhere, $"{label}的暂停时长不能超过 24 小时"));

                        if (budget.Times < 1)
                            issues.Add(Error(ruleWhere, $"{label}的暂停次数至少是 1 次"));
                    }
                }

                // A whitelist rule allows, so it needs no policy; everything else must name one.
                //
                // 「还没绑」是提醒，「绑到一个不存在的」才是错误 —— 区别是后者是写错了，前者是
                // 还没写到。以前两者都是 Error，于是**新建出来的规则永远存不下去**：
                // 它出生时本来就没有策略，而保存时只要有一个 Error 就整次拒绝。
                if (rule.Match.Type != MatchKind.Whitelist)
                {
                    if (string.IsNullOrWhiteSpace(rule.PolicyId))
                        issues.Add(Warning(ruleWhere, "还没有绑策略 —— 命中之后不会执行任何动作"));
                    else if (!policyIds.Contains(rule.PolicyId))
                        issues.Add(Error(ruleWhere, $"PolicyId '{rule.PolicyId}' does not exist"));
                }

                if (!string.IsNullOrWhiteSpace(rule.ScheduleId) && !scheduleIds.Contains(rule.ScheduleId))
                    issues.Add(Error(ruleWhere, $"ScheduleId '{rule.ScheduleId}' does not exist"));

                // 同上：一条刚建出来的规则一定还没有匹配内容。它是"还没填"，不是"填错了"。
                // 以前这里也是 Error，结果是**按「新建」的那一下必定报"配置没有保存"** ——
                // 用户根本加不了规则。
                if (rule.Match.Patterns.Count == 0 || rule.Match.Patterns.All(string.IsNullOrWhiteSpace))
                {
                    // 「模型监督」那一支里 Patterns 存的是那句禁令，不是匹配式 —— 说"没有匹配式"
                    // 会让人去找一个根本不存在的输入框。
                    issues.Add(Warning(ruleWhere, rule.Match.Type == MatchKind.Model
                        ? "还没有写要禁止自己看什么 —— 这条规则现在不会命中"
                        : "还没有填匹配内容 —— 这条规则现在不会命中"));
                }

                foreach (var pattern in rule.Match.Patterns)
                {
                    if (rule.Match.Type == MatchKind.Regex && !IsValidRegex(pattern))
                        issues.Add(Error(ruleWhere, $"pattern is not a valid regex: {pattern}"));
                }

                var fields = rule.Match.Fields.Count > 0
                    ? rule.Match.Fields
                    : new List<MatchField> { MatchField.Url, MatchField.Title };

                if (fields.Contains(MatchField.Body) && !target.Reader.ScanBody)
                    issues.Add(Warning(ruleWhere, "matches Body but this target has ScanBody=false — the rule can never fire on body text"));

                if (fields.Contains(MatchField.Url) && !target.Reader.ScanUrl)
                    issues.Add(Warning(ruleWhere, "matches Url but this target has ScanUrl=false"));

                if (fields.Contains(MatchField.Title) && !target.Reader.ScanTitle)
                    issues.Add(Warning(ruleWhere, "matches Title but this target has ScanTitle=false"));
            }
        }

        return issues;
    }

    private static ConfigIssue Error(string where, string message) => new(IssueSeverity.Error, where, message);

    private static ConfigIssue Warning(string where, string message) => new(IssueSeverity.Warning, where, message);

    private static bool IsValidRegex(string pattern)
    {
        try
        {
            _ = new Regex(pattern);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsValidTime(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && (TimeSpan.TryParse(value, out var parsed) && parsed >= TimeSpan.Zero && parsed < TimeSpan.FromDays(1)
            || TryParseHourMinute(value, out _));

    private static bool TryParseHourMinute(string value, out TimeSpan result)
    {
        result = default;
        var parts = value.Split(':');
        if (parts.Length != 2
            || !int.TryParse(parts[0], out var hour)
            || !int.TryParse(parts[1], out var minute)
            || hour is < 0 or > 23
            || minute is < 0 or > 59)
        {
            return false;
        }

        result = new TimeSpan(hour, minute, 0);
        return true;
    }
}
