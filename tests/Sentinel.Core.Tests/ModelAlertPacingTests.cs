using Sentinel.Core.Model;
using Xunit;

namespace Sentinel.Core.Tests;

/// <summary>
/// 提醒的节奏 —— 屏幕上到底多久弹一次。
///
/// <para>
/// 这一条被用户直接看见，而且踩过两次坑，所以它得是被测对象，不能只是一句注释：
/// </para>
/// <list type="number">
/// <item><description>
/// "每 30 秒检查一次 + 距离上次够 30 秒才弹"，间隔和判据是<b>同一个数</b> —— 定时器的下一跳从
/// 这一跳开始算，而"上次弹的时刻"记在这一跳之后，相减必然略小于一个间隔，于是每隔一跳就被判成
/// "还没到"，实际节奏翻倍成 60 秒。
/// </description></item>
/// <item><description>
/// 探测要跑一分钟，那一分钟里如果因为"正在探"就不弹，间隔会被拉得更长。
/// </description></item>
/// </list>
/// <para>
/// 所以这里用假时钟按真实节奏（每 <see cref="ModelAlertPacing.CheckEvery"/> 一跳）走，直接量
/// <b>两次提醒之间隔了多久</b>。
/// </para>
/// </summary>
public sealed class ModelAlertPacingTests
{
    /// <summary>只有测试推它才会走的时钟。</summary>
    private sealed class FakeClock
    {
        public FakeClock() => Now = new DateTimeOffset(2026, 10, 16, 9, 0, 0, TimeSpan.FromHours(8));

        public DateTimeOffset Now { get; private set; }

        public void Advance(TimeSpan by) => Now += by;
    }

    /// <summary>按真实节奏走一段时间的"检查"，收集每一次弹出提醒的时刻。</summary>
    private static async Task<List<TimeSpan>> RunAsync(
        TimeSpan span, Func<ModelAlertPacing, Task<PacingStep>> tick)
    {
        var clock = new FakeClock();
        var start = clock.Now;
        var pacing = new ModelAlertPacing(() => clock.Now);
        var alerts = new List<TimeSpan>();

        for (var i = 0; i < (int)(span / ModelAlertPacing.CheckEvery); i++)
        {
            clock.Advance(ModelAlertPacing.CheckEvery);

            if (await tick(pacing) == PacingStep.Alert)
                alerts.Add(clock.Now - start);
        }

        return alerts;
    }

    /// <summary>
    /// 验节奏。<paramref name="expected"/> 是总次数，里面<b>第一次是立刻弹的</b>（刚发现坏了就该说
    /// 一声，不用等一个周期），所以之后每一次的间隔都要落在 [repeat, repeat + 一次检查] 里。
    /// </summary>
    private static void AssertCadence(IReadOnlyList<TimeSpan> alerts, TimeSpan repeat, int expected)
    {
        Assert.Equal(expected, alerts.Count);

        for (var i = 1; i < alerts.Count; i++)
        {
            var gap = alerts[i] - alerts[i - 1];

            // 下界：一次都没提前弹（提前就说明判据松了）。上界：到点之后最多再等一次检查。
            Assert.InRange(gap, repeat, repeat + ModelAlertPacing.CheckEvery);
        }
    }

    [Fact]
    public void 检查要比要催的节奏勤_不然每两跳才弹一次()
    {
        // 判据和节奏相等，就是那个"实际只有一半"的坑。这一条把它钉住。
        Assert.True(ModelAlertPacing.CheckEvery < ModelAlertPacing.UrgentRepeat);
    }

    [Fact]
    public async Task 慢到拿不到判定_每三十秒催一次()
    {
        var alerts = await RunAsync(TimeSpan.FromMinutes(20),
            pacing => pacing.StepAsync(ModelTroubleKind.TooSlow, () => Task.FromResult(false)));

        // 立刻一次，然后每 30 秒一次：二十分钟一共 40 次。探测那几次一次都不占掉提醒。
        AssertCadence(alerts, ModelAlertPacing.UrgentRepeat, 40);
    }

    [Fact]
    public async Task 本地服务没起来_也是每三十秒催一次()
    {
        var alerts = await RunAsync(TimeSpan.FromMinutes(10),
            pacing => pacing.StepAsync(ModelTroubleKind.LocalUnreachable, () => Task.FromResult(false)));

        AssertCadence(alerts, ModelAlertPacing.UrgentRepeat, 20);
    }

    [Fact]
    public async Task 要你去处理那类_五分钟催一次()
    {
        // 钥匙不对、欠费这类是外面的事，催也没用 —— 但必须说，不然模型监督已经停了而你不知道。
        var alerts = await RunAsync(TimeSpan.FromMinutes(20),
            pacing => pacing.StepAsync(ModelTroubleKind.NeedsAttention, () => Task.FromResult(false)));

        AssertCadence(alerts, ModelAlertPacing.NeedsAttentionRepeat, 4);
    }

    [Fact]
    public async Task 探测在外面飞的那一分钟里也照催不误()
    {
        // 真实里探测要跑一分钟（慢模型上一次全尺寸判定就是这么久）。如果那一分钟里因为"正在探"
        // 就不弹，节奏会从 30 秒滑到 90 秒以上 —— 用户看到的就是"说是 30 秒，其实没有"。
        var clock = new FakeClock();
        var pacing = new ModelAlertPacing(() => clock.Now);

        // 第一次：探测开始，卡住不回来
        var gate = new TaskCompletionSource<bool>();
        var probing = pacing.StepAsync(ModelTroubleKind.TooSlow, () => gate.Task);

        Assert.False(probing.IsCompleted);
        Assert.True(pacing.ProbeInFlight);

        // 探测还没回来，定时器照样每 5 秒跳一次 —— 到 30 秒就该弹第一次
        clock.Advance(ModelAlertPacing.UrgentRepeat);
        Assert.Equal(PacingStep.Alert,
            await pacing.StepAsync(ModelTroubleKind.TooSlow, () => gate.Task));

        clock.Advance(ModelAlertPacing.UrgentRepeat);
        Assert.Equal(PacingStep.Alert,
            await pacing.StepAsync(ModelTroubleKind.TooSlow, () => gate.Task));

        // 探测终于回来了：还是慢（返回 false）。刚弹过，所以这一轮不再补一次。
        gate.SetResult(false);
        Assert.Equal(PacingStep.Nothing, await probing);
        Assert.False(pacing.ProbeInFlight);
    }

    [Fact]
    public async Task 太慢那类探得稀_不一边抱怨它慢一边让它满负荷()
    {
        // 「太慢」的探测本身就是一次全尺寸判定，慢模型上要跑一分钟。十分钟里探 5 次、
        // 催 20 次：提醒是免费的，探测不是。
        var probes = 0;

        var alerts = await RunAsync(TimeSpan.FromMinutes(10),
            pacing => pacing.StepAsync(ModelTroubleKind.TooSlow, () =>
            {
                probes++;
                return Task.FromResult(false);
            }));

        Assert.Equal(5, probes);
        Assert.Equal(20, alerts.Count);
    }

    [Fact]
    public async Task 连不上那类随便探_它是瞬间失败()
    {
        var probes = 0;

        await RunAsync(TimeSpan.FromMinutes(10),
            pacing => pacing.StepAsync(ModelTroubleKind.LocalUnreachable, () =>
            {
                probes++;
                return Task.FromResult(false);
            }));

        // 每 30 秒一次（连接被拒是瞬间的），十分钟 20 次。
        Assert.Equal(20, probes);
    }

    [Fact]
    public async Task 探测通了就闭嘴_而且下次出问题立刻说()
    {
        var clock = new FakeClock();
        var pacing = new ModelAlertPacing(() => clock.Now);

        // 探头一次就说"好了"
        clock.Advance(ModelAlertPacing.CheckEvery);
        Assert.Equal(PacingStep.Nothing,
            await pacing.StepAsync(ModelTroubleKind.TooSlow, () => Task.FromResult(true)));

        // 好了之后的检查什么都不做 —— 不能因为记着上一次的计时就接着弹
        clock.Advance(ModelAlertPacing.CheckEvery);
        Assert.Equal(PacingStep.Nothing,
            await pacing.StepAsync(ModelTroubleKind.None, () => Task.FromResult(true)));

        // 又坏了：第一次检查就该弹，而不是接着上一次的计时再等 30 秒
        clock.Advance(ModelAlertPacing.CheckEvery);
        Assert.Equal(PacingStep.Alert,
            await pacing.StepAsync(ModelTroubleKind.TooSlow, () => Task.FromResult(false)));
    }
}
