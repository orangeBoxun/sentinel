namespace Sentinel.Core.Model;

/// <summary>这一轮该干什么。</summary>
public enum PacingStep
{
    /// <summary>什么都不用做。</summary>
    Nothing,

    /// <summary>先探一次 —— 探的是"现在到底通不通、够不够快"。</summary>
    Probe,

    /// <summary>弹提醒。</summary>
    Alert,
}

/// <summary>
/// 模型监督停摆期间，提醒的节奏：<b>什么时候催、什么时候探</b>。
///
/// <para>
/// 为什么值得单独一个类：这两件事的"次数"是要拿证据说话的（用户看到的是屏幕，不是日志），
/// 而原来它们和 <c>DispatcherTimer</c>、<c>DateTimeOffset.Now</c>、以及一句 <c>await</c> 混在
/// 一起 —— 没法验，也就没验过。抽出来之后，<b>时钟和探测都是参数</b>，于是"每 30 秒一次"
/// 是一句跑得出来的话，不再是一句注释（见 ModelAlertPacingTests）。
/// </para>
/// <para>
/// 规则本身：
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>除了"没网"那类**一律 30 秒一次**</b>。原来钥匙/欠费那类要五分钟才说一次，理由是
/// "外面的事，催也没用" —— 但真正会碰到的是<b>免费在线模型被限流、被拒绝</b>：
/// 网好好的、网站打得开、他照样能刷，只是没人判了。五分钟一次起不到"逼他换一个能用的
/// 模型"的作用，而那个作用正是他要的。
/// </description></item>
/// <item><description>
/// <b>"没网"是唯一不催的一类</b>（见 <see cref="ModelTroubleKind.Offline"/>）：那时网站
/// 自己也打不开，为一件不会发生的事每 30 秒弹一次只会制造噪音。探测照旧，网一回来接着吵。
/// </description></item>
/// <item><description>
/// <b>探得比催稀</b>：探测是一次真正的判定请求，会占住那台机器（慢模型上一探就是一分钟）。
/// "太慢"那一类每 <b>2 分钟</b>探一次就够；其余几类失败是**瞬间**的（4xx/5xx/连接被拒），
/// 30 秒探一次几乎不花钱。
/// </description></item>
/// </list>
/// </summary>
public sealed class ModelAlertPacing
{
    /// <summary>
    /// 多久催一次 —— <b>30 秒</b>，停在哪一类上都一样。
    ///
    /// <para>
    /// 只有一个数，是因为"多快能修好"不是分类的依据：限流、欠费、地址写错、模型太慢，
    /// 用户在同一个界面里都能换一个服务商解决。而唯一不能立刻解决的是"整台机器没网"，
    /// 那种情况下提醒里的话会说清楚（连不上），不该因此把提醒放稀。
    /// </para>
    /// </summary>
    public static readonly TimeSpan RepeatEvery = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 其余几类的探测间隔：30 秒。
    ///
    /// <para>
    /// 这一类的失败是**瞬间**的（4xx/429/5xx/连接被拒），探测发出去马上就有结果，几乎不花时间。
    /// 它唯一的代价是"也算一次请求" —— 但停摆期间真判定请求全停了，所以总共也就每分钟两次。
    /// 值得：用户换掉服务商之后，最多半分钟它就知道不用再吵了。
    /// </para>
    /// </summary>
    public static readonly TimeSpan CheapProbeEvery = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 「太慢」那类的探测间隔：2 分钟。
    ///
    /// <para>
    /// 比上面那个稀得多，因为这一类的探测<b>本身就是一次全尺寸判定</b>（真实禁令 + 2000 字正文，
    /// 慢模型上要跑一分钟才超时）。探得勤就成了"一边抱怨它慢，一边让它满负荷"。
    /// </para>
    /// <para>
    /// 代价说清楚：你换完模型之后，最多要等两分钟它才肯相信，这两分钟里提醒还会响。
    /// 换成 90 秒能让它早点闭嘴，但那等于把那台机器的一半时间花在探测上。
    /// </para>
    /// </summary>
    public static readonly TimeSpan SlowProbeEvery = TimeSpan.FromMinutes(2);

    /// <summary>
    /// 多久看一眼状态 —— <b>比 <see cref="RepeatEvery"/> 细一档</b>。
    ///
    /// <para>
    /// 为什么不留成一个周期一跳：这一跳里可能先跑了一次探测（<b>最长一分钟</b>），而"上次提醒的
    /// 时刻"是那之后才记下的 —— 下一次比较时距那个时刻就少了一大截。判据一旦落空（<c>&lt;</c> 了就
    /// 不弹），代价是<b>整整一个周期</b>：30 秒的节奏变成 60 秒，正是"说是 30 秒其实没有"的样子。
    /// 检查比节奏细一档，落空最多多等一跳。
    /// </para>
    /// <para>
    /// 顺带记一笔实测（timerprobe，300 ms 名义间隔 × 60 跳）：<c>DispatcherTimer</c> 这种
    /// 定时器只会<b>晚</b>不会早 —— 两跳之间实测 301.4 ~ 359.3 ms，一跳都没被判成"还没到"。
    /// 所以余量本身够，不需要在这里做补偿；但不该把余量押在一个一毫秒级的边界上。
    /// </para>
    /// </summary>
    public static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(5);

    private readonly Func<DateTimeOffset> _now;

    private DateTimeOffset? _shown;
    private DateTimeOffset? _probed;
    private bool _probing;

    /// <param name="now">取当前时刻。测试传一个能推的假时钟进来。</param>
    public ModelAlertPacing(Func<DateTimeOffset>? now = null) =>
        _now = now ?? (() => DateTimeOffset.Now);

    /// <summary>这一次探测回来了没有 —— 探测要几十秒，而检查每 5 秒一次。</summary>
    public bool ProbeInFlight => _probing;

    /// <summary>
    /// 好起来了：把计时归零。
    ///
    /// <para>
    /// 不清 <see cref="_probing"/>：那说的是"真的有一个请求在外面飞"，和计时不是一回事 ——
    /// 它会在自己那个 <c>finally</c> 里落地。<c>Reset</c> 之后探测回来发现还是坏的，
    /// 下一次检查就会立刻提醒（那是对的：状态刚刚变过，值得马上说一声）。
    /// </para>
    /// </summary>
    public void Reset()
    {
        _shown = null;
        _probed = null;
    }

    public static TimeSpan ProbeEveryFor(ModelTroubleKind kind) => kind switch
    {
        // 探测本身就是一次全尺寸判定，慢模型上一探就是一分钟 —— 稀一点。
        ModelTroubleKind.TooSlow => SlowProbeEvery,

        // 网断着的时候探测会立刻失败（发不出去），但"网回来了没有"只能靠它认出来。
        // 两分钟一次足够，而且真去探之前会先问一句有没有网（见 ModelSupervisor.ProbeAsync）。
        ModelTroubleKind.Offline => SlowProbeEvery,

        _ => CheapProbeEvery,
    };

    /// <summary>
    /// 走一轮检查。返回 <see cref="PacingStep.Alert"/> 表示"现在该弹全屏了"。
    ///
    /// <para>
    /// <b>探测优先于提醒</b>，顺序不能反：提醒每 30 秒就到期一次，而探测间隔更长，先看提醒的话
    /// 探测永远排不上队 —— "它已经好了"这条判据就再也不更新了。
    /// </para>
    /// <para>
    /// <b>探测在外面飞的时候，照催不误。</b>这是同一个坑的另一半：探测要一分钟，如果那一分钟里
    /// 因为"正在探"就不弹，提醒的间隔会被拉成 90 秒以上，而人看到的就是"说是 30 秒，其实没有"。
    /// </para>
    /// </summary>
    /// <param name="probe">真去问一次"现在通不通"。返回 true 表示好了（这一轮就不提醒了）。</param>
    public async Task<PacingStep> StepAsync(ModelTroubleKind kind, Func<Task<bool>> probe)
    {
        if (kind == ModelTroubleKind.None)
        {
            Reset();
            return PacingStep.Nothing;
        }

        if (!_probing && Elapsed(_probed) >= ProbeEveryFor(kind))
        {
            _probed = _now();
            _probing = true;

            bool healed;
            try
            {
                healed = await probe();
            }
            finally
            {
                _probing = false;
            }

            if (healed)
            {
                Reset();
                return PacingStep.Nothing;
            }
        }

        // **网断着的时候不催。** 网站自己也打不开，没什么可拦的；这时候每 30 秒弹一次全屏
        // 只会把人训练成"看见提醒就关掉"，而那个习惯一旦养成，真正该看的那条也就不起作用了。
        // 探测照旧（上面那段），网一回来该吵的会接着吵。
        if (kind == ModelTroubleKind.Offline)
            return PacingStep.Nothing;

        if (Elapsed(_shown) < RepeatEvery)
            return PacingStep.Nothing;

        _shown = _now();
        return PacingStep.Alert;
    }

    /// <summary>距离上一次过了多久。没记过 = 无穷久（第一次一定到点）。</summary>
    private TimeSpan Elapsed(DateTimeOffset? at) =>
        at is null ? TimeSpan.MaxValue : _now() - at.Value;
}
