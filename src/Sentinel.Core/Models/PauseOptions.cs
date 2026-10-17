namespace Sentinel.Core.Models;

/// <summary>
/// 有额度的那点例外：这条规则每天可以被放下几次，每次几分钟 —— 不需要任何人的许可。
///
/// <para>
/// 关掉监控的其它每一种做法都是"放宽"，放宽要令牌。这个口子存在，是因为"有边界的暂停"的替代品
/// 不是"用户就专心了"，而是用户去摸令牌、或者去摸卸载程序。给个有额度的让步，花掉的是冲动，
/// 不是那把锁。
/// </para>
/// <para>
/// 它挂在目标而不是整个程序上，因为冲动的形状就是这样：一个应用、一个晚上。全局额度会被
/// 最先伸手的那个目标花光，剩下的就一无所有了。
/// </para>
/// <para>
/// 所以它坐在执行的那一侧：允许暂停、允许停得更频繁、允许停得更久，三种都是放宽，
/// 三种都要令牌。
/// </para>
/// </summary>
public sealed class PauseOptions
{
    public bool Enabled { get; set; }

    /// <summary>
    /// 每天最多能暂停几次。按<b>本机日历日</b>算，过零点重新计数。
    ///
    /// <para>
    /// 以前是"每 N 天一次"（N 是整数），于是最少就是一天一次 —— 上午十点停过一次，之后每天
    /// 都得等到十点，一天里再没有第二次机会。按天给额度更贴近冲动的形状：晚上想歇一会儿，
    /// 歇完接着来就是了，不用把明天的额度也搭进去。代价说清楚：它会跨零点重置，
    /// 所以 23:59 和 00:01 可以连着停两次 —— 那是"按天记"的必然结果。
    /// </para>
    /// </summary>
    public int TimesPerDay { get; set; } = 1;

    /// <summary>How long one pause lasts.</summary>
    public int Minutes { get; set; } = 10;

    public override string ToString() =>
        Enabled ? $"每天最多 {TimesPerDay} 次，每次 {Minutes} 分钟" : "暂停：关";
}
