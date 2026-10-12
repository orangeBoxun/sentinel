using Contract = Sentinel.Core.Ipc.Ipc;
using Sentinel.Core.Models;

namespace Sentinel.Core.Config;

public enum ChangeDirection
{
    /// <summary>Nothing about enforcement changed (renaming, a reminder's wording, an unused policy).</summary>
    Neutral,

    /// <summary>The rules got stricter: monitoring more, blocking more, reacting sooner.</summary>
    Tightening,

    /// <summary>The rules got weaker. This is what an unlock token is required for.</summary>
    Relaxing,
}

/// <summary>
/// Decides whether a config edit makes enforcement weaker, stronger, or neither.
///
/// The unlock-token requirement hangs on this: tightening is free, relaxing needs a token. Two
/// properties matter more than precision here.
///
/// <para>
/// <b>The default is Relaxing.</b> Anything unclassifiable counts as a relaxation. Being wrong that
/// way costs a token; being wrong the other way means a restriction quietly comes off, which is the
/// failure this whole mechanism exists to prevent.
/// </para>
/// <para>
/// <b>A save is relaxing if any part of it is.</b> One tightening change must not be able to
/// smuggle a relaxation through alongside it.
/// </para>
/// </summary>
public static class ConfigRelaxation
{
    public static ChangeDirection Classify(AppConfig previous, AppConfig current)
    {
        var fold = new Fold();

        // Safe mode off is the whole point of the switch: true is the *less* restrictive state.
        fold.Flag(previous.Ui.DryRun, current.Ui.DryRun, restrictiveWhenTrue: false);

        // Turning logging off removes the record of what happened.
        fold.Flag(previous.Logging.LogUrlHash, current.Logging.LogUrlHash);
        fold.Flag(previous.Logging.LogBodyText, current.Logging.LogBodyText);

        fold.Flag(previous.Protection.Enabled, current.Protection.Enabled);

        if (previous.Protection.DelaySeconds != current.Protection.DelaySeconds)
        {
            fold.Note(current.Protection.DelaySeconds > previous.Protection.DelaySeconds
                ? ChangeDirection.Tightening
                : ChangeDirection.Relaxing);
        }

        // Changing the mode or the secret can only be reported as "not clearly stricter".
        if (previous.Protection.Mode != current.Protection.Mode
            || previous.Protection.SecretHash != current.Protection.SecretHash)
        {
            fold.Note(ChangeDirection.Relaxing);
        }

        fold.Note(CompareTargets(previous, current));
        fold.Note(ComparePolicies(previous, current));
        fold.Note(CompareSchedules(previous, current));
        fold.Note(CompareModelSupervision(previous, current));

        return fold.Value;
    }

    /// <summary>
    /// 只看"模型监督还开着吗"，不看具体是哪个服务商。
    ///
    /// <para>
    /// <b>停用它 = 放宽。</b>这是整个模型监督唯一能被绕开的地方：没有这条，把 ActiveModelId 清空
    /// 就等于关掉了所有模型规则，而界面上一切照旧 —— 比改规则隐蔽得多，因为改规则至少还会
    /// 在列表里看得见。
    /// </para>
    /// <para>
    /// <b>换服务商不算。</b>那是配置：地址、钥匙、模型名都只是"用哪条管道"。把它也算成放宽，
    /// 会让换一次 key 都要去拿令牌 —— 而那件事既不削弱也不加强任何监督。
    /// </para>
    /// </summary>
    private static ChangeDirection CompareModelSupervision(AppConfig previous, AppConfig current)
    {
        var before = !string.IsNullOrWhiteSpace(previous.ActiveModelId);
        var after = !string.IsNullOrWhiteSpace(current.ActiveModelId);

        return before == after
            ? ChangeDirection.Neutral
            : after
                ? ChangeDirection.Tightening    // 开起来了
                : ChangeDirection.Relaxing;     // 关掉了
    }

    /// <summary>
    /// 按 Id 建索引。**重复的 Id 不能让判定崩掉** —— 判定的职责是"放行或拒绝"，
    /// 抛异常会把一次保存变成一次崩溃（而配置里出现重复 Id 是有可能的：手改过，
    /// 或者旧版本留下的）。重复时取第一个：判定只问"有没有放宽"，取谁都一样。
    /// </summary>
    private static Dictionary<string, T> ById<T>(IEnumerable<T> items, Func<T, string> id) =>
        items.GroupBy(id).ToDictionary(g => g.Key, g => g.First());

    // ------------------------------------------------------------------ 说清是哪一处放宽

    /// <summary>
    /// 放宽的是哪几处 —— 按人话列出来。
    ///
    /// <para>
    /// 拒绝时只能说一句"会放宽限制"，而<b>判定器本来就知道是哪一处</b>，那句话却把它丢了。
    /// 于是人对着屏幕猜："我又不是白名单，为什么要令牌" —— 而真正放宽的往往是顺手动的另一处
    /// （改了优先级、取消勾选了一个字段、把匹配内容删了一条……）。
    /// </para>
    /// <para>
    /// 做法是<b>逐项试</b>：造一份"只改这一处"的配置，问一次 <see cref="Classify"/>。
    /// 解释和判定因此同源 —— 不另写一套判定，就不会出现"理由说放宽、判定说不是"这种事。
    /// 只在已经确定这次是放宽之后才调用它，多出来的开销只落在被拒的那一次上。
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> Explain(AppConfig previous, AppConfig current)
    {
        var whys = new List<string>();

        // "只改这一处"算不算放宽。每一处都拿判定器本人来问。
        bool Widens(Action<AppConfig> edit)
        {
            var probe = Contract.Deserialize<AppConfig>(Contract.Serialize(previous));

            if (probe is null)
                return false;

            edit(probe);
            return Classify(previous, probe) == ChangeDirection.Relaxing;
        }

        Target TargetOf(AppConfig config, string id) => config.Targets.First(t => t.Id == id);
        Rule RuleOf(AppConfig config, string targetId, string ruleId) =>
            TargetOf(config, targetId).Rules.First(r => r.Id == ruleId);
        Policy PolicyOf(AppConfig config, string id) => config.Policies.First(p => p.Id == id);
        Schedule ScheduleOf(AppConfig config, string id) => config.Schedules.First(s => s.Id == id);

        // ---- 全局开关
        if (Widens(c => c.Ui.DryRun = current.Ui.DryRun))
            whys.Add("打开了「空跑」—— 规则照判，但不真的拦");

        if (Widens(c => c.Logging.LogUrlHash = current.Logging.LogUrlHash))
            whys.Add("不再记录网址");

        if (Widens(c => c.Logging.LogBodyText = current.Logging.LogBodyText))
            whys.Add("不再记录正文");

        if (Widens(c => c.Protection.Enabled = current.Protection.Enabled))
            whys.Add("关掉了启动保护");

        if (Widens(c => c.Protection.DelaySeconds = current.Protection.DelaySeconds))
            whys.Add($"启动保护的等待时间从 {previous.Protection.DelaySeconds} 秒改成 {current.Protection.DelaySeconds} 秒");

        if (Widens(c => c.ActiveModelId = current.ActiveModelId))
            whys.Add("关掉了模型监督");

        // ---- 目标
        var beforeTargets = ById(previous.Targets, t => t.Id);
        var afterTargets = ById(current.Targets, t => t.Id);

        foreach (var id in beforeTargets.Keys.Except(afterTargets.Keys))
        {
            if (Widens(c => c.Targets.RemoveAll(t => t.Id == id)))
                whys.Add($"不再监控「{beforeTargets[id].DisplayName}」");
        }

        foreach (var (id, after) in afterTargets)
        {
            if (!beforeTargets.TryGetValue(id, out var before))
            {
                // 新增的目标只有在它本身是"放行"那一类时才可能算放宽，交给判定器答。
                if (Widens(c => c.Targets.Add(after)))
                    whys.Add($"新增的目标「{after.DisplayName}」是放行用的");

                continue;
            }

            var label = after.DisplayName;

            if (Widens(c => TargetOf(c, id).Enabled = after.Enabled))
                whys.Add($"不再监控「{label}」");

            if (Widens(c => TargetOf(c, id).ProcessNames = after.ProcessNames))
                whys.Add($"「{label}」不再盯进程 {string.Join("、", before.ProcessNames.Except(after.ProcessNames))}");

            if (Widens(c => TargetOf(c, id).WindowTitlePattern = after.WindowTitlePattern))
                whys.Add($"「{label}」的标题过滤改了（原本是「{before.WindowTitlePattern ?? "不限制"}」）");

            if (Widens(c => TargetOf(c, id).Reader.IntervalMs = after.Reader.IntervalMs))
                whys.Add($"「{label}」的读取间隔从 {before.Reader.IntervalMs} 毫秒放到 {after.Reader.IntervalMs} 毫秒");

            if (before.Reader.ScanUrl && !after.Reader.ScanUrl && Widens(c => TargetOf(c, id).Reader.ScanUrl = false))
                whys.Add($"「{label}」不再读网址");

            if (before.Reader.ScanTitle && !after.Reader.ScanTitle && Widens(c => TargetOf(c, id).Reader.ScanTitle = false))
                whys.Add($"「{label}」不再读标题");

            if (before.Reader.ScanBody && !after.Reader.ScanBody && Widens(c => TargetOf(c, id).Reader.ScanBody = false))
                whys.Add($"「{label}」不再读正文");

            if (Widens(c => TargetOf(c, id).Reader.MaxBodyChars = after.Reader.MaxBodyChars))
                whys.Add($"「{label}」的正文只读前 {after.Reader.MaxBodyChars} 个字（原来是 {before.Reader.MaxBodyChars}）");

            // ---- 这个目标下的规则
            foreach (var gone in before.Rules.Where(r => after.Rules.All(x => x.Id != r.Id)))
            {
                var ruleId = gone.Id;

                if (Widens(c => TargetOf(c, id).Rules.RemoveAll(r => r.Id == ruleId)))
                    whys.Add($"「{label}」里删掉了规则「{gone.Name}」");
            }

            foreach (var rule in after.Rules)
            {
                var older = before.Rules.FirstOrDefault(r => r.Id == rule.Id);
                var ruleId = rule.Id;

                if (older is null)
                {
                    if (Widens(c => TargetOf(c, id).Rules.Add(rule)))
                        whys.Add($"「{label}」里新增的规则「{rule.Name}」是放行用的");

                    continue;
                }

                var ruleLabel = $"规则「{rule.Name}」（{label}）";

                if (Widens(c => RuleOf(c, id, ruleId).Enabled = rule.Enabled))
                    whys.Add($"{ruleLabel}被停用了");

                if (Widens(c => RuleOf(c, id, ruleId).Match.Type = rule.Match.Type))
                    whys.Add($"{ruleLabel}的匹配方式从 {older.Match.Type} 改成 {rule.Match.Type}");

                if (Widens(c => RuleOf(c, id, ruleId).Match.Patterns = rule.Match.Patterns))
                    whys.Add($"{ruleLabel}的匹配内容少了 {string.Join("、", older.Match.Patterns.Except(rule.Match.Patterns))}");

                if (Widens(c => RuleOf(c, id, ruleId).Match.Fields = rule.Match.Fields))
                    whys.Add($"{ruleLabel}不再匹配 {string.Join("、", older.Match.Fields.Except(rule.Match.Fields))} 这些字段");

                if (Widens(c => RuleOf(c, id, ruleId).Match.CaseSensitive = rule.Match.CaseSensitive))
                    whys.Add($"{ruleLabel}改成区分大小写（这样能匹配到的更少）");

                if (Widens(c => RuleOf(c, id, ruleId).Priority = rule.Priority))
                    whys.Add($"{ruleLabel}的优先级从 {older.Priority} 改成 {rule.Priority}");

                if (Widens(c => RuleOf(c, id, ruleId).PolicyId = rule.PolicyId))
                    whys.Add($"{ruleLabel}换成了别的策略（{older.PolicyId} → {rule.PolicyId}）");

                if (Widens(c => RuleOf(c, id, ruleId).ScheduleId = rule.ScheduleId))
                    whys.Add($"{ruleLabel}的生效时间从「{older.ScheduleId ?? "始终生效"}」改成「{rule.ScheduleId ?? "始终生效"}」");

                if (Widens(c => RuleOf(c, id, ruleId).Pause.Enabled = rule.Pause.Enabled)
                    || Widens(c => RuleOf(c, id, ruleId).Pause.EveryDays = rule.Pause.EveryDays)
                    || Widens(c => RuleOf(c, id, ruleId).Pause.Minutes = rule.Pause.Minutes))
                {
                    whys.Add($"{ruleLabel}的暂停设置放宽了（现在是每 {rule.Pause.EveryDays} 天可以停 {rule.Pause.Minutes} 分钟）");
                }
            }
        }

        // ---- 策略
        var beforePolicies = ById(previous.Policies, p => p.Id);
        var afterPolicies = ById(current.Policies, p => p.Id);

        foreach (var id in beforePolicies.Keys.Except(afterPolicies.Keys))
        {
            if (Widens(c => c.Policies.RemoveAll(p => p.Id == id)))
                whys.Add($"删掉了策略「{beforePolicies[id].Name}」");
        }

        foreach (var (id, after) in afterPolicies)
        {
            // 新增策略本身不放宽任何东西，直到有规则指向它 —— 那一步会算在规则上。
            if (!beforePolicies.TryGetValue(id, out var before))
                continue;

            var label = $"策略「{after.Name}」";

            if (Widens(c => PolicyOf(c, id).Actions = after.Actions))
                whys.Add($"{label}少了动作 {string.Join("、", before.Actions.Except(after.Actions))}");

            if (Widens(c => PolicyOf(c, id).GraceSeconds = after.GraceSeconds))
                whys.Add($"{label}的宽限时间从 {before.GraceSeconds} 秒改成 {after.GraceSeconds} 秒");

            if (Widens(c => PolicyOf(c, id).CooldownSeconds = after.CooldownSeconds))
                whys.Add($"{label}的冷却时间从 {before.CooldownSeconds} 秒改成 {after.CooldownSeconds} 秒");

            if (Widens(c => PolicyOf(c, id).DebounceCount = after.DebounceCount))
                whys.Add($"{label}的命中次数从 {before.DebounceCount} 改成 {after.DebounceCount}");

            if (Widens(c => PolicyOf(c, id).RepeatSeconds = after.RepeatSeconds))
                whys.Add($"{label}的重复提醒从 {before.RepeatSeconds} 秒改成 {after.RepeatSeconds} 秒");

            if (Widens(c => PolicyOf(c, id).AlertStyle = after.AlertStyle))
                whys.Add($"{label}的提醒方式从 {before.AlertStyle} 改成 {after.AlertStyle}");
        }

        // ---- 时间表
        var beforeSchedules = ById(previous.Schedules, s => s.Id);
        var afterSchedules = ById(current.Schedules, s => s.Id);

        foreach (var id in beforeSchedules.Keys.Except(afterSchedules.Keys))
        {
            if (Widens(c => c.Schedules.RemoveAll(s => s.Id == id)))
                whys.Add($"删掉了时间表「{beforeSchedules[id].Id}」");
        }

        foreach (var (id, after) in afterSchedules)
        {
            if (!beforeSchedules.TryGetValue(id, out var before))
                continue;

            if (Widens(c => ScheduleOf(c, id).DaysOfWeek = after.DaysOfWeek))
                whys.Add($"时间表「{id}」生效的日子少了（原本是 {before.DaysOfWeek.Count} 天）");

            if (Widens(c => ScheduleOf(c, id).TimeWindows = after.TimeWindows))
                whys.Add($"时间表「{id}」生效的时段短了");
        }

        return whys;
    }

    /// <summary>True when the edit needs an unlock token.</summary>
    public static bool RequiresToken(AppConfig previous, AppConfig current) =>
        Classify(previous, current) == ChangeDirection.Relaxing;

    // ------------------------------------------------------------------ 目标

    private static ChangeDirection CompareTargets(AppConfig previous, AppConfig current)
    {
        var fold = new Fold();
        var before = ById(previous.Targets, t => t.Id);
        var after = ById(current.Targets, t => t.Id);

        foreach (var _ in after.Keys.Except(before.Keys))
            fold.Note(ChangeDirection.Tightening);   // one more app being watched

        foreach (var _ in before.Keys.Except(after.Keys))
            fold.Note(ChangeDirection.Relaxing);     // an app stops being watched

        foreach (var id in before.Keys.Intersect(after.Keys))
            fold.Note(CompareTarget(before[id], after[id]));

        return fold.Value;
    }

    private static ChangeDirection CompareTarget(Target before, Target after)
    {
        var fold = new Fold();

        fold.Flag(before.Enabled, after.Enabled);
        fold.Superset(before.ProcessNames, after.ProcessNames);

        // A title pattern narrows which windows count, so gaining one restricts and losing one does not.
        if (before.WindowTitlePattern != after.WindowTitlePattern)
        {
            if (string.IsNullOrEmpty(before.WindowTitlePattern))
                fold.Note(ChangeDirection.Tightening);
            else
                fold.Note(ChangeDirection.Relaxing);   // removed, or replaced with something else
        }

        fold.Flag(before.Reader.ScanUrl, after.Reader.ScanUrl);
        fold.Flag(before.Reader.ScanTitle, after.Reader.ScanTitle);
        fold.Flag(before.Reader.ScanBody, after.Reader.ScanBody);

        if (before.Reader.IntervalMs != after.Reader.IntervalMs)
        {
            fold.Note(after.Reader.IntervalMs < before.Reader.IntervalMs
                ? ChangeDirection.Tightening
                : ChangeDirection.Relaxing);
        }

        if (before.Reader.MaxBodyChars != after.Reader.MaxBodyChars)
        {
            fold.Note(after.Reader.MaxBodyChars > before.Reader.MaxBodyChars
                ? ChangeDirection.Tightening
                : ChangeDirection.Relaxing);
        }

        fold.Note(CompareRules(before, after));
        return fold.Value;
    }

    /// <summary>
    /// The pause allowance is a weakening written down: allowing one at all, allowing them more
    /// often, or allowing longer ones are all ways of loosening, so all three need a token. The
    /// bookkeeping that spends them lives with the Service, so this cannot be edited into an
    /// unlimited supply.
    ///
    /// <para>
    /// Compared per rule, where the allowance now lives. Rules that only exist on one side are
    /// already counted by <see cref="CompareRules"/>: a brand-new rule is a tightening, so its
    /// default (pause off) cannot be a back door, and a deleted rule is a relaxation anyway.
    /// </para>
    /// </summary>
    private static ChangeDirection ComparePause(PauseOptions before, PauseOptions after)
    {
        var fold = new Fold();

        fold.Flag(before.Enabled, after.Enabled, restrictiveWhenTrue: false);

        if (before.EveryDays != after.EveryDays)
            fold.Note(after.EveryDays < before.EveryDays ? ChangeDirection.Relaxing : ChangeDirection.Tightening);

        if (before.Minutes != after.Minutes)
            fold.Note(after.Minutes > before.Minutes ? ChangeDirection.Relaxing : ChangeDirection.Tightening);

        return fold.Value;
    }

    // ------------------------------------------------------------------ 规则

    private static ChangeDirection CompareRules(Target before, Target after)
    {
        var fold = new Fold();
        var b = ById(before.Rules, r => r.Id);
        var a = ById(after.Rules, r => r.Id);

        foreach (var id in a.Keys.Except(b.Keys))
        {
            // A new whitelist rule exists to let things through, so it weakens rather than
            // strengthens — the opposite of every other kind of rule.
            fold.Note(a[id].Match.Type == MatchKind.Whitelist
                ? ChangeDirection.Relaxing
                : ChangeDirection.Tightening);
        }

        foreach (var _ in b.Keys.Except(a.Keys))
            fold.Note(ChangeDirection.Relaxing);

        foreach (var id in b.Keys.Intersect(a.Keys))
            fold.Note(CompareRule(b[id], a[id]));

        return fold.Value;
    }

    private static ChangeDirection CompareRule(Rule before, Rule after)
    {
        var fold = new Fold();

        // Everything about a whitelist rule reads backwards: it exists to permit, so growing it or
        // enabling it loosens enforcement.
        var permitting = after.Match.Type == MatchKind.Whitelist;

        fold.Flag(before.Enabled, after.Enabled, restrictiveWhenTrue: !permitting);
        fold.Growth(before.Match.Patterns, after.Match.Patterns, stricterWhenGrowing: !permitting);
        fold.Growth(before.Match.Fields, after.Match.Fields, stricterWhenGrowing: !permitting);

        // Case-sensitive matching covers strictly less text.
        fold.Flag(before.Match.CaseSensitive, after.Match.CaseSensitive, restrictiveWhenTrue: false);

        // Reinterpreting the same patterns as a different kind of match cannot be judged stricter.
        if (before.Match.Type != after.Match.Type)
            fold.Note(ChangeDirection.Relaxing);

        // A schedule narrows when the rule applies, so gaining one weakens it — the opposite of how
        // an Enabled flag reads.
        if (before.ScheduleId != after.ScheduleId)
        {
            fold.Note(string.IsNullOrEmpty(after.ScheduleId) && !string.IsNullOrEmpty(before.ScheduleId)
                ? ChangeDirection.Tightening   // dropped its schedule: now always on
                : ChangeDirection.Relaxing);   // gained one, or swapped to another
        }

        // Which policy runs, and in what order the rules are tried, both change outcomes in ways
        // that cannot be called stricter.
        if (before.PolicyId != after.PolicyId)
            fold.Note(ChangeDirection.Relaxing);

        if (before.Priority != after.Priority)
            fold.Note(ChangeDirection.Relaxing);

        // Reminder is text shown to the user. It changes nothing about enforcement.

        fold.Note(ComparePause(before.Pause, after.Pause));

        return fold.Value;
    }

    // ------------------------------------------------------------------ 策略

    private static ChangeDirection ComparePolicies(AppConfig previous, AppConfig current)
    {
        var fold = new Fold();
        var before = ById(previous.Policies, p => p.Id);
        var after = ById(current.Policies, p => p.Id);

        // Adding one changes nothing until a rule points at it, and that shows up on the rule.
        foreach (var _ in before.Keys.Except(after.Keys))
            fold.Note(ChangeDirection.Relaxing);

        foreach (var id in before.Keys.Intersect(after.Keys))
            fold.Note(ComparePolicy(before[id], after[id]));

        return fold.Value;
    }

    private static ChangeDirection ComparePolicy(Policy before, Policy after)
    {
        var fold = new Fold();

        fold.Superset(before.Actions, after.Actions);

        if (before.GraceSeconds != after.GraceSeconds)
            fold.Note(after.GraceSeconds < before.GraceSeconds ? ChangeDirection.Tightening : ChangeDirection.Relaxing);

        if (before.CooldownSeconds != after.CooldownSeconds)
            fold.Note(after.CooldownSeconds < before.CooldownSeconds ? ChangeDirection.Tightening : ChangeDirection.Relaxing);

        if (before.DebounceCount != after.DebounceCount)
            fold.Note(after.DebounceCount < before.DebounceCount ? ChangeDirection.Tightening : ChangeDirection.Relaxing);

        // 0 means "never repeat". Losing the repeat is losing reminders.
        if (before.RepeatSeconds != after.RepeatSeconds)
        {
            var wasOn = before.RepeatSeconds > 0;
            var isOn = after.RepeatSeconds > 0;

            if (wasOn != isOn)
                fold.Note(isOn ? ChangeDirection.Tightening : ChangeDirection.Relaxing);
            else if (isOn)
                fold.Note(after.RepeatSeconds < before.RepeatSeconds ? ChangeDirection.Tightening : ChangeDirection.Relaxing);
        }

        if (before.AlertStyle != after.AlertStyle)
            fold.Note(after.AlertStyle == AlertStyle.Fullscreen ? ChangeDirection.Tightening : ChangeDirection.Relaxing);

        return fold.Value;
    }

    // ------------------------------------------------------------------ 时间表

    private static ChangeDirection CompareSchedules(AppConfig previous, AppConfig current)
    {
        var fold = new Fold();
        var before = ById(previous.Schedules, s => s.Id);
        var after = ById(current.Schedules, s => s.Id);

        // Adding or removing one changes nothing until a rule references it — and the reference
        // change is caught on the rule. But a schedule that stays and shrinks its windows narrows
        // when its rules apply, which widens the time they do not.
        foreach (var id in before.Keys.Intersect(after.Keys))
            fold.Note(CompareSchedule(before[id], after[id]));

        return fold.Value;
    }

    private static ChangeDirection CompareSchedule(Schedule before, Schedule after)
    {
        var fold = new Fold();

        fold.Superset(before.DaysOfWeek, after.DaysOfWeek);

        if (!before.TimeWindows.SequenceEqual(after.TimeWindows))
        {
            // Windows are the hours a rule is ON. Only a strict increase in covered time is stricter;
            // a shift that merely moves the same hours somewhere else is not.
            fold.Note(CoveredMinutes(after.TimeWindows) > CoveredMinutes(before.TimeWindows)
                ? ChangeDirection.Tightening
                : ChangeDirection.Relaxing);
        }

        return fold.Value;
    }

    private static int CoveredMinutes(IEnumerable<TimeWindow> windows)
    {
        var total = 0.0;

        foreach (var window in windows)
        {
            if (!TimeSpan.TryParse(window.Start, out var start) || !TimeSpan.TryParse(window.End, out var end))
                continue;

            var span = end - start;
            if (span < TimeSpan.Zero)
                span += TimeSpan.FromDays(1);   // across midnight

            total += span.TotalMinutes;
        }

        return (int)total;
    }

    // ------------------------------------------------------------------ 折叠

    /// <summary>
    /// Accumulates directions. Relaxing is sticky: once anything weakens, the whole comparison is a
    /// relaxation no matter what else changed alongside it.
    /// </summary>
    private sealed class Fold
    {
        public ChangeDirection Value { get; private set; } = ChangeDirection.Neutral;

        public void Note(ChangeDirection direction)
        {
            if (direction == ChangeDirection.Relaxing)
                Value = ChangeDirection.Relaxing;
            else if (direction == ChangeDirection.Tightening && Value == ChangeDirection.Neutral)
                Value = ChangeDirection.Tightening;
        }

        /// <param name="restrictiveWhenTrue">False for flags where true means *less* restricted.</param>
        public void Flag(bool before, bool after, bool restrictiveWhenTrue = true)
        {
            if (before == after)
                return;

            var gainedRestriction = restrictiveWhenTrue ? after : !after;
            Note(gainedRestriction ? ChangeDirection.Tightening : ChangeDirection.Relaxing);
        }

        /// <summary>Adding entries is stricter; removing any is a relaxation.</summary>
        public void Superset<T>(IReadOnlyCollection<T> before, IReadOnlyCollection<T> after) =>
            Growth(before, after, stricterWhenGrowing: true);

        /// <summary>
        /// For lists where growing means "more of the same". A whitelist's patterns read the other
        /// way round: adding one lets another thing through.
        /// </summary>
        public void Growth<T>(IReadOnlyCollection<T> before, IReadOnlyCollection<T> after, bool stricterWhenGrowing)
        {
            if (after.Except(before).Any())
                Note(stricterWhenGrowing ? ChangeDirection.Tightening : ChangeDirection.Relaxing);

            if (before.Except(after).Any())
                Note(stricterWhenGrowing ? ChangeDirection.Relaxing : ChangeDirection.Tightening);
        }
    }
}
