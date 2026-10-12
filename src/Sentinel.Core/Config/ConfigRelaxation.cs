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

    /// <summary>True when the edit needs an unlock token.</summary>
    public static bool RequiresToken(AppConfig previous, AppConfig current) =>
        Classify(previous, current) == ChangeDirection.Relaxing;

    // ------------------------------------------------------------------ 目标

    private static ChangeDirection CompareTargets(AppConfig previous, AppConfig current)
    {
        var fold = new Fold();
        var before = previous.Targets.ToDictionary(t => t.Id);
        var after = current.Targets.ToDictionary(t => t.Id);

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
        var b = before.Rules.ToDictionary(r => r.Id);
        var a = after.Rules.ToDictionary(r => r.Id);

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
        var before = previous.Policies.ToDictionary(p => p.Id);
        var after = current.Policies.ToDictionary(p => p.Id);

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
        var before = previous.Schedules.ToDictionary(s => s.Id);
        var after = current.Schedules.ToDictionary(s => s.Id);

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
