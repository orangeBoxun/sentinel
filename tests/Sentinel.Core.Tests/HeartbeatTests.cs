using Sentinel.Core.Ipc;
using Xunit;

namespace Sentinel.Core.Tests;

/// <summary>
/// 心跳里带的那点东西，以及"这个会话算不算出问题了"这条规则。
///
/// <para>
/// 这条规则错了很难发现：要么每隔十秒杀一个健康的 Agent，要么永远才发现不了停摆。
/// 两种都不会报错，只会表现为"监控有时候莫名其妙断一下"。
/// </para>
/// </summary>
public sealed class HeartbeatTests
{
    private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan StallTimeout = TimeSpan.FromMinutes(3);

    private static readonly DateTimeOffset Now = new(2026, 10, 12, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 不带时刻的心跳就是一个会话号()
    {
        // 这一条是给"不是监控循环的调用方"用的（比如保险库窗口探服务在不在）。
        Assert.Equal("7", HeartbeatPayload.Format(7, null));
    }

    [Fact]
    public void 带时刻的心跳能原样读回来()
    {
        var tick = Now.AddSeconds(-5);
        var text = HeartbeatPayload.Format(7, tick);

        Assert.True(HeartbeatPayload.TryParse(text, out var payload));
        Assert.Equal(7, payload.SessionId);
        Assert.Equal(tick, payload.LastTick);
    }

    [Fact]
    public void 老格式还能读出来_只是没有进度信息()
    {
        // 升级过程中新服务会遇到老 Agent。读不懂就把人家当卡死的杀掉，是最糟的结果。
        Assert.True(HeartbeatPayload.TryParse("7", out var payload));
        Assert.Equal(7, payload.SessionId);
        Assert.Null(payload.LastTick);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("abc|2026-10-12T18:00:00+00:00")]
    public void 读不懂的心跳不当作有效(string? text)
    {
        Assert.False(HeartbeatPayload.TryParse(text, out _));
    }

    [Fact]
    public void 时刻读不出来就算没有而不是算对的()
    {
        // "7|昨晚" 这种：会话号有效，时刻无效 —— 当成"没带进度信息"，不是当成一个有效时刻。
        Assert.True(HeartbeatPayload.TryParse("7|昨晚", out var payload));
        Assert.Equal(7, payload.SessionId);
        Assert.Null(payload.LastTick);
    }

    // ------------------------------------------------------------------ 该不该认为它出问题了

    [Fact]
    public void 从来没心跳过就算出问题了()
    {
        Assert.True(AgentLiveness.IsStale(null, null, Now, HeartbeatTimeout, StallTimeout));
    }

    [Fact]
    public void 心跳和循环都新鲜就正常()
    {
        Assert.False(AgentLiveness.IsStale(
            Now.AddSeconds(-5), Now.AddSeconds(-1), Now, HeartbeatTimeout, StallTimeout));
    }

    [Fact]
    public void 心跳停了就算出问题了()
    {
        Assert.True(AgentLiveness.IsStale(
            Now.AddSeconds(-30), Now.AddSeconds(-1), Now, HeartbeatTimeout, StallTimeout));
    }

    [Fact]
    public void 心跳还发着但循环停摆了_也算出问题了()
    {
        // 这正是最难发现的那种：进程活着、心跳照发、托盘图标在，但规则已经不生效了。
        // 心跳跑在自己的线程上，所以"心跳正常"完全说明不了监控在跑。
        Assert.True(AgentLiveness.IsStale(
            Now.AddSeconds(-5), Now.AddMinutes(-5), Now, HeartbeatTimeout, StallTimeout));
    }

    [Fact]
    public void 不报进度的老Agent不能因此被判成卡死()
    {
        // 最要紧的一条：老版本不报"循环走到哪儿了"。把"没有这项信息"当成"停了"，会让新服务
        // 在升级过程中不停杀掉还在正常工作的老 Agent。
        Assert.False(AgentLiveness.IsStale(
            Now.AddSeconds(-5), null, Now, HeartbeatTimeout, StallTimeout));
    }

    [Fact]
    public void 刚过心跳超时的那一刻还不算_到点才算()
    {
        // 差一秒到点：还活着（lastSeen = 现在往前 19 秒）。
        Assert.False(AgentLiveness.IsStale(
            Now - HeartbeatTimeout + TimeSpan.FromSeconds(1), null, Now, HeartbeatTimeout, StallTimeout));

        // 正好到点：算。
        Assert.True(AgentLiveness.IsStale(
            Now - HeartbeatTimeout, null, Now, HeartbeatTimeout, StallTimeout));
    }
}
