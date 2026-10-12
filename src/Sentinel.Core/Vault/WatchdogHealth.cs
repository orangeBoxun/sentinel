namespace Sentinel.Core.Vault;

/// <summary>
/// 看门狗还活着吗。
///
/// <para>
/// 单独拿出来是为了**能测**，也因为这条规则的两种错法都很贵：误判会让保险箱无缘无故封印
/// （只有监护人码能开，而监护人码在别的设备上），漏判则让"把看门狗停掉"变成一条没人管的
/// 捷径。
/// </para>
/// </summary>
public static class WatchdogHealth
{
    /// <summary>
    /// 看门狗的存活记录已经过期了吗。
    ///
    /// <para>
    /// <b><paramref name="lastAlive"/> 为 null（从来没有过这个文件）时返回 false。</b>
    /// 这一点是刻意的：新装的机器、或者装的时候用了 <c>-NoWatchdog</c>，都不该被当成"看门狗被
    /// 停掉了" —— 那会让保险箱在一次正常安装之后就被封印，而封印意味着只能用监护人码打开。
    /// 只有"见过它、后来不见了"才是有人动过手。
    /// </para>
    /// </summary>
    public static bool IsGone(DateTimeOffset? lastAlive, DateTimeOffset now, TimeSpan tolerance) =>
        lastAlive is { } at && now - at >= tolerance;
}
