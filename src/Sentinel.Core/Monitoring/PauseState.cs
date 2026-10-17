using System.Text.Json;
using Sentinel.Core.Models;

namespace Sentinel.Core.Monitoring;

/// <summary>What the Service sends back when a pause is granted.</summary>
public sealed record PauseReply(string Until, string NextAvailableAt, int Minutes, int LeftToday);

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
/// 记三样：最近一次暂停的开始时刻（用来算"现在是不是正在暂停"）、那一次属于本机的哪一天、
/// 那一天已经用掉几次。额度按<b>本机日历日</b>算、过零点重新计数 —— 这是"每天最多 N 次"的
/// 直接实现，也顺带解决了"每 N 天一次"那种会一路往后漂的冷却窗口（上午十点停一次，
/// 之后每天都要等到十点才算"到了"）。
/// </para>
/// <para>
/// 不另记"这次暂停什么时候结束"：它等于开始时刻 + 配置的长度，记两份只会多出一种两者不一致的
/// 可能。
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

    /// <summary>规则 id → 最近一次暂停所在的本机日期（<c>yyyy-MM-dd</c>）。</summary>
    public Dictionary<string, string> Day { get; set; } = new();

    /// <summary>规则 id → 那一天的额度已经用掉几次。</summary>
    public Dictionary<string, int> Count { get; set; } = new();

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
            Day.Clear();
            Count.Clear();
        }
    }

    public bool IsPaused(string ruleId, PauseOptions options, DateTimeOffset now) =>
        PausedUntil(ruleId, options, now) is not null;

    /// <summary>When the pause in progress ends, or null when there is not one.</summary>
    public DateTimeOffset? PausedUntil(string ruleId, PauseOptions options, DateTimeOffset now) =>
        LastPaused(ruleId) is { } last && now < last.AddMinutes(options.Minutes)
            ? last.AddMinutes(options.Minutes)
            : null;

    /// <summary>今天还剩几次。过零点就是新的一天 —— 昨天用掉的不算。</summary>
    public int LeftToday(string ruleId, PauseOptions options, DateTimeOffset now)
    {
        var used = DayOfKey(ruleId) == DayOf(now) ? CountOf(ruleId) : 0;
        var left = options.TimesPerDay - used;

        return left < 0 ? 0 : left;
    }

    /// <summary>
    /// 下一次可以暂停的时刻；<b>null 表示现在就可以</b>。
    ///
    /// <para>
    /// 两条限制，取较晚的那个：今天的额度用完了要等明天零点；正在暂停中则要等这一轮结束 ——
    /// 后者保证"再点一次暂停"不会把额度花在一段还没走完的暂停上。
    /// </para>
    /// </summary>
    public DateTimeOffset? AvailableAt(string ruleId, PauseOptions options, DateTimeOffset now)
    {
        var quotaReady = LeftToday(ruleId, options, now) > 0 ? now : TomorrowStart(now);
        var pauseEnd = PausedUntil(ruleId, options, now) ?? now;

        var at = quotaReady > pauseEnd ? quotaReady : pauseEnd;

        return at <= now ? null : at;
    }

    public bool CanPause(string ruleId, PauseOptions options, DateTimeOffset now) =>
        AvailableAt(ruleId, options, now) is null;

    public void Record(string ruleId, DateTimeOffset now)
    {
        var day = DayOf(now);

        Count[ruleId] = DayOfKey(ruleId) == day ? CountOf(ruleId) + 1 : 1;
        Day[ruleId] = day;
        Last[ruleId] = now.ToString("O");
    }

    private DateTimeOffset? LastPaused(string ruleId) =>
        Last.TryGetValue(ruleId, out var value) && DateTimeOffset.TryParse(value, out var parsed)
            ? parsed
            : null;

    private string? DayOfKey(string ruleId) => Day.TryGetValue(ruleId, out var day) ? day : null;

    private int CountOf(string ruleId) => Count.TryGetValue(ruleId, out var count) ? count : 0;

    /// <summary>本机日历日的文本形式。都在同一台机器上算，所以按字符串比较就够。</summary>
    private static string DayOf(DateTimeOffset now) => now.ToString("yyyy-MM-dd");

    /// <summary>下一个本机零点。</summary>
    private static DateTimeOffset TomorrowStart(DateTimeOffset now) =>
        new(now.Date.AddDays(1), now.Offset);
}
