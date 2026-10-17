using System.Text.Json;
using Sentinel.Core.Models;

namespace Sentinel.Core.Monitoring;

/// <summary>What the Service sends back when a pause is granted.</summary>
/// <param name="Scope">哪一套额度、哪一期（比如「工作日 · 今天」）—— 提醒里要用它说话。</param>
public sealed record PauseReply(
    string Until, string NextAvailableAt, int Minutes, int Left, string Scope);

/// <summary>一条规则在某一套额度里用掉了多少。</summary>
public sealed class PauseUse
{
    /// <summary>这一期是哪一期（见 <see cref="PausePeriods.Key"/>）。期号变了 = 新的一期。</summary>
    public string Key { get; set; } = "";

    /// <summary>这一期已经用掉几次。</summary>
    public int Count { get; set; }
}

/// <summary>
/// 每条规则暂停过几次、什么时候。
///
/// <para>
/// Deliberately not part of the config file. The pause is the one weakening that does not need a
/// token, so if its bookkeeping lived in the config, editing the config would be a way of granting
/// yourself an unlimited supply of them — the record would sit next to the thing it is supposed to
/// be recording. It lives in the install directory instead: readable by the user, writable only by
/// the Service.
/// </para>
/// <para>
/// 记四样：最近一次暂停的开始时刻、那一次批了多少分钟（<b>当场定下，之后改配置不影响这一轮</b>）、
/// 以及工作日/周末两套额度各自用掉多少。额度按本机日历的周期算（每天/每周/每月），期号一变
/// 就是新的一期。
/// </para>
/// <para>
/// 不记"这次暂停什么时候结束"：它等于开始时刻 + 批过的那几分钟。真记两份，只会多出一种两者
/// 不一致的可能。
/// </para>
/// </summary>
public sealed class PauseState
{
    public const string FileName = ".paused.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>规则 id → 最近一次暂停的开始时刻，ISO 8601。老版本的文件只有这一项。</summary>
    public Dictionary<string, string> Last { get; set; } = new();

    /// <summary>规则 id → 那一次批了多少分钟。老文件里没有这一项，读成 0（= 那一轮已经结束）。</summary>
    public Dictionary<string, int> LastMinutes { get; set; } = new();

    /// <summary>规则 id → 工作日那套额度用到哪儿了。</summary>
    public Dictionary<string, PauseUse> Workday { get; set; } = new();

    /// <summary>规则 id → 周末那套额度用到哪儿了。</summary>
    public Dictionary<string, PauseUse> Weekend { get; set; } = new();

    public static string PathFor(string configPath) =>
        Path.Combine(Path.GetDirectoryName(configPath) ?? ".", FileName);

    public static PauseState Load(string configPath)
    {
        try
        {
            var path = PathFor(configPath);
            return File.Exists(path)
                ? JsonSerializer.Deserialize<PauseState>(File.ReadAllText(path), Options) ?? new PauseState()
                : new PauseState();
        }
        catch
        {
            // Unreadable reads as "never paused", which fails toward enforcing.
            return new PauseState();
        }
    }

    public void Save(string configPath)
    {
        var path = PathFor(configPath);

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
        }
        catch
        {
            // A pause that could not be written down is not one. Failing loudly here would let a
            // full disk turn into an unlocked monitor.
            Last.Clear();
            LastMinutes.Clear();
            Workday.Clear();
            Weekend.Clear();
        }
    }

    // ------------------------------------------------------------------ 正在暂停

    public bool IsPaused(string ruleId, DateTimeOffset now) => PausedUntil(ruleId, now) is not null;

    /// <summary>这一轮暂停什么时候结束；没有正在进行的暂停就是 null。</summary>
    public DateTimeOffset? PausedUntil(string ruleId, DateTimeOffset now) =>
        Last.TryGetValue(ruleId, out var value)
        && DateTimeOffset.TryParse(value, out var last)
        && now < last.AddMinutes(MinutesOf(ruleId))
            ? last.AddMinutes(MinutesOf(ruleId))
            : null;

    /// <summary>上一轮批了几分钟（老文件没有这一项 → 0）。</summary>
    public int MinutesOf(string ruleId) => LastMinutes.TryGetValue(ruleId, out var minutes) ? minutes : 0;

    // ------------------------------------------------------------------ 额度

    /// <summary>当前这一套、这一期还剩几次。</summary>
    public int LeftNow(string ruleId, PauseOptions options, DateTimeOffset now)
    {
        var budget = options.For(now);
        var used = UsedIn(budget.Period, UseOf(ruleId, now), now);
        var left = budget.Times - used;

        return left < 0 ? 0 : left;
    }

    /// <summary>
    /// 下一次可以暂停的时刻；<b>null 表示现在就可以</b>。
    ///
    /// <para>
    /// 两条限制，取较晚的那个：这一期的额度用完了就等下一期开始；正在暂停中则要等这一轮结束 ——
    /// 后者保证"再点一次暂停"不会把额度花在一段还没走完的暂停上。
    /// </para>
    /// </summary>
    public DateTimeOffset? AvailableAt(string ruleId, PauseOptions options, DateTimeOffset now)
    {
        var budget = options.For(now);
        var used = UsedIn(budget.Period, UseOf(ruleId, now), now);

        var quotaReady = used < budget.Times ? now : PausePeriods.Next(budget.Period, now);
        var pauseEnd = PausedUntil(ruleId, now) ?? now;

        var at = quotaReady > pauseEnd ? quotaReady : pauseEnd;

        return at <= now ? null : at;
    }

    public bool CanPause(string ruleId, PauseOptions options, DateTimeOffset now) =>
        AvailableAt(ruleId, options, now) is null;

    /// <summary>花掉一次额度，并且把这一轮的长度定下来。</summary>
    public void Record(string ruleId, PauseOptions options, DateTimeOffset now)
    {
        var budget = options.For(now);
        var use = UseOf(ruleId, now);
        var key = PausePeriods.Key(budget.Period, now);

        var samePeriod = use.Key == key;
        use.Key = key;
        use.Count = samePeriod ? use.Count + 1 : 1;

        Last[ruleId] = now.ToString("O");
        LastMinutes[ruleId] = budget.Minutes;
    }

    /// <summary>给人看的说法：现在用的是哪一套、哪一期（「工作日 · 今天」）。</summary>
    public static string Scope(PauseOptions options, DateTimeOffset now)
    {
        var budget = options.For(now);

        return $"{(PausePeriods.IsWorkday(now) ? "工作日" : "周末")} · {PausePeriods.Label(budget.Period)}";
    }

    // ------------------------------------------------------------------ 内部

    /// <summary>现在这一刻该用哪一套账。按开始那一刻算，和 <see cref="PauseOptions.For"/> 一致。</summary>
    private PauseUse UseOf(string ruleId, DateTimeOffset now)
    {
        var bucket = PausePeriods.IsWorkday(now) ? Workday : Weekend;

        if (!bucket.TryGetValue(ruleId, out var use))
        {
            use = new PauseUse();
            bucket[ruleId] = use;
        }

        return use;
    }

    private static int UsedIn(PausePeriod period, PauseUse use, DateTimeOffset now) =>
        use.Key == PausePeriods.Key(period, now) ? use.Count : 0;
}
