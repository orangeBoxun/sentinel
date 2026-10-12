using Sentinel.Core.Abstractions;
using Sentinel.Core.Model;
using Sentinel.Core.Models;

namespace Sentinel.Core.Engine;

/// <summary>
/// v1 judge: evaluates a target's rules in priority order (higher first).
/// The first matching keyword/regex rule yields Block; a matching whitelist rule yields Allow.
/// A model rule yields Block once the model has said so, and nothing until then.
/// </summary>
public sealed class RuleEngine : IJudge
{
    private readonly IReadOnlyDictionary<string, Schedule> _schedules;

    /// <summary>
    /// 可空的：没有模型监督的时候这个引擎还是干原来那些事。
    /// </summary>
    private readonly IModelSupervisor? _models;

    public RuleEngine(IEnumerable<Schedule> schedules, IModelSupervisor? models = null)
    {
        _schedules = schedules.ToDictionary(s => s.Id);
        _models = models;
    }

    public string Name => "rule-engine";

    public Verdict Judge(JudgeContext context) => Evaluate(context.Target, context.Snapshot, context.Now);

    public Verdict Evaluate(Target target, ContentSnapshot snapshot, DateTimeOffset now)
    {
        foreach (var rule in target.Rules.Where(r => r.Enabled).OrderByDescending(r => r.Priority))
        {
            if (rule.ScheduleId is not null
                && _schedules.TryGetValue(rule.ScheduleId, out var schedule)
                && !ScheduleEvaluator.IsActive(schedule, now))
            {
                continue;
            }

            // 模型那一支走别的路：它没有"匹配"这一步，只有"判过没有"。
            if (rule.Match.Type == MatchKind.Model)
            {
                if (JudgeByModel(rule, snapshot) is { } decided)
                    return decided;

                continue;
            }

            if (!MatchEvaluator.Matches(rule.Match, snapshot))
                continue;

            if (rule.Match.Type == MatchKind.Whitelist)
                return Verdict.Allow();

            return Verdict.Block(rule.Id, $"matched rule '{rule.Name}'", rule.PolicyId);
        }

        return Verdict.Allow();
    }

    /// <summary>
    /// 模型那一支。返回 null 表示"这条规则这一次不表态"，交给下一条规则。
    ///
    /// <para>
    /// <b>还没判出来就先放行，这是有意的。</b>模型调用要几秒，而循环是同步的 —— 等着它会把
    /// 整台机器上的拦截一起卡住。代价是第一次命中会晚几秒才拦；换来的是这段时间里别的东西
    /// 照常工作。
    /// </para>
    /// <para>
    /// 判不出来（没网、欠费、回来的东西看不懂）也一样是放行。方向是刻意选的：这类失败
    /// 大多发生在"你自己也上不了网"的时候，而误拦一次的代价远大于晚拦一次。
    /// </para>
    /// </summary>
    private Verdict? JudgeByModel(Rule rule, ContentSnapshot snapshot)
    {
        if (_models is null)
            return null;   // 没接监督，这条规则当它不存在

        var verdict = _models.Lookup(rule.Id, snapshot);

        if (verdict.IsBlocked)
            return Verdict.Block(rule.Id, verdict.Reason ?? "模型判定违规", rule.PolicyId);

        if (verdict.Kind == ModelVerdictKind.Unknown)
        {
            // 规则的 Patterns 在这一支里不是匹配式，而是"我要禁止自己看什么"那句话。
            _models.Request(rule.Id, string.Join("\n", rule.Match.Patterns), snapshot);
        }

        return null;
    }
}
