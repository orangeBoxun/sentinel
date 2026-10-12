using Sentinel.Core.Vault;
using Xunit;

namespace Sentinel.Core.Tests;

/// <summary>
/// 「看门狗还活着吗」这条规则。
///
/// <para>
/// 两种错法都很贵，所以两个方向都要钉住：误判会让保险箱无缘无故封印（只有监护人码能开，
/// 而那串码在另一台设备上）；漏判则让"把看门狗停掉"变成一条没人管的捷径。
/// </para>
/// </summary>
public sealed class WatchdogHealthTests
{
    private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 从来没有过心跳不算被停掉()
    {
        // **最要紧的一条。** 新装的机器、或者装的时候用了 -NoWatchdog，都没有这个文件；
        // 把它当成"看门狗被停掉了"，会让保险箱在任何一次正常安装之后就被封印 ——
        // 而封印意味着只能用监护人码打开。
        Assert.False(WatchdogHealth.IsGone(null, Now, Tolerance));
    }

    [Fact]
    public void 心跳还在就不算()
    {
        Assert.False(WatchdogHealth.IsGone(Now.AddSeconds(-30), Now, Tolerance));
    }

    [Fact]
    public void 差一秒到点还不算_到点才算()
    {
        Assert.False(WatchdogHealth.IsGone(Now - Tolerance + TimeSpan.FromSeconds(1), Now, Tolerance));
        Assert.True(WatchdogHealth.IsGone(Now - Tolerance, Now, Tolerance));
    }

    [Fact]
    public void 心跳停在很久以前就算被停掉了()
    {
        Assert.True(WatchdogHealth.IsGone(Now.AddHours(-1), Now, Tolerance));
    }
}
