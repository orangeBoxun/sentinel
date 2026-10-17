namespace Sentinel.Core.Models;

/// <summary>额度的结算周期：多久重置一次。</summary>
public enum PausePeriod
{
    /// <summary>每天 —— 本机零点。</summary>
    Day,

    /// <summary>每周 —— 周一零点（按国内习惯，一周从周一开始）。</summary>
    Week,

    /// <summary>每月 —— 1 号零点。</summary>
    Month,
}

/// <summary>一套额度：每个周期几次、每次多久。</summary>
public sealed class PauseBudget
{
    public PausePeriod Period { get; set; } = PausePeriod.Day;

    /// <summary>这个周期里最多能暂停几次。</summary>
    public int Times { get; set; } = 1;

    /// <summary>这个周期里每次暂停多久。</summary>
    public int Minutes { get; set; } = 10;

    public override string ToString() => $"{PausePeriods.Word(Period)} {Times} 次、每次 {Minutes} 分钟";
}

/// <summary>
/// 有额度的例外：这条规则在工作日、周末各有多少次机会停下来，每次多久 —— 不需要任何人的许可。
///
/// <para>
/// 关掉监控的其它每一种做法都是"放宽"，放宽要令牌。这个口子存在，是因为"有边界的暂停"的替代品
/// 不是"用户就专心了"，而是用户去摸令牌、或者去摸卸载程序。给个有额度的让步，花掉的是冲动，
/// 不是那把锁。
/// </para>
/// <para>
/// <b>为什么分工作日和周末两套</b>：同一个人对这两天的要求本来就不一样 —— 工作日想严一点
/// （一天就一次，五分钟），周末想松一点（一天三次，半个小时）。一套额度要么把工作日放得太松，
/// 要么把周末勒得太死。
/// </para>
/// <para>
/// <b>周期为什么是每天/每周/每月</b>：周期和"次数"是两个独立的旋钮。想要"每个工作日一次"就是
/// 工作日那套选"每天"、填 1；想要"一周总共三次"就是选"每周"、填 3。只给"每天"的话，
/// 想要更长的节奏就没法表达。
/// </para>
/// <para>
/// 所以它坐在执行的那一侧：允许暂停、允许停得更频繁、允许停得更久，三种都是放宽，
/// 三种都要令牌。
/// </para>
/// </summary>
public sealed class PauseOptions
{
    public bool Enabled { get; set; }

    /// <summary>周一到周五用这一套。</summary>
    public PauseBudget Workday { get; set; } = new();

    /// <summary>周六、周日用这一套。</summary>
    public PauseBudget Weekend { get; set; } = new();

    /// <summary>这个时刻用的是哪一套 —— 按<b>开始那一刻</b>算，跨零点不换套。</summary>
    public PauseBudget For(DateTimeOffset now) => PausePeriods.IsWorkday(now) ? Workday : Weekend;

    public override string ToString() =>
        Enabled
            ? $"工作日 {Workday}；周末 {Weekend}"
            : "暂停：关";
}

/// <summary>
/// 周期的算术：怎么排期号（同一期里额度是同一份）、下一期从什么时候开始、以及给人看的说法。
///
/// <para>
/// 都按<b>本机日历</b>算，不用滚动窗口（"最近 7 天"那种）：滚动窗口在界面上说不清 ——
/// 用户问"我今天还能停几次"，滚动窗口的答案取决于上周同一天停没停过。
/// </para>
/// </summary>
public static class PausePeriods
{
    /// <summary>周一到周五算工作日。节假日不特判。</summary>
    public static bool IsWorkday(DateTimeOffset now) =>
        now.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);

    /// <summary>这一期从什么时候开始 —— 同一期里额度是同一份。</summary>
    public static DateTimeOffset Start(PausePeriod period, DateTimeOffset now) => period switch
    {
        PausePeriod.Week => new DateTimeOffset(Monday(now), now.Offset),
        PausePeriod.Month => new DateTimeOffset(new DateTime(now.Year, now.Month, 1), now.Offset),
        _ => new DateTimeOffset(now.Date, now.Offset),
    };

    /// <summary>下一期从什么时候开始 —— 额度用完就是等这个时刻。</summary>
    public static DateTimeOffset Next(PausePeriod period, DateTimeOffset now) => period switch
    {
        PausePeriod.Week => Start(period, now).AddDays(7),
        PausePeriod.Month => new DateTimeOffset(new DateTime(now.Year, now.Month, 1).AddMonths(1), now.Offset),
        _ => new DateTimeOffset(now.Date.AddDays(1), now.Offset),
    };

    /// <summary>期号。期号变了就算新的一期，额度是满的。</summary>
    public static string Key(PausePeriod period, DateTimeOffset now) =>
        period == PausePeriod.Month ? now.ToString("yyyy-MM") : Start(period, now).ToString("yyyy-MM-dd");

    /// <summary>给人看的周期名：「今天」「本周」「本月」。</summary>
    public static string Label(PausePeriod period) => period switch
    {
        PausePeriod.Week => "本周",
        PausePeriod.Month => "本月",
        _ => "今天",
    };

    /// <summary>配置里那个下拉框的说法。</summary>
    public static string Word(PausePeriod period) => period switch
    {
        PausePeriod.Week => "每周",
        PausePeriod.Month => "每月",
        _ => "每天",
    };

    /// <summary>本周一（零点）。</summary>
    private static DateTime Monday(DateTimeOffset now) =>
        now.Date.AddDays(-(((int)now.DayOfWeek + 6) % 7));
}
