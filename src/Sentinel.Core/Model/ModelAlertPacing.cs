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
/// <b>要你去处理</b>的（钥匙、欠费）：5 分钟一次。外面的事，催也没用。
/// </description></item>
/// <item><description>
/// <b>你自己的服务没起来</b>、<b>慢到拿不到判定</b>：<b>30 秒一次</b>。这两类最好办
/// （拉起来、或者换个模型），而它们和"规则等于不存在"是同一件事 —— 界面上却看不出区别。
/// </description></item>
/// <item><description>
/// <b>探得比催稀</b>：探测是一次真正的判定请求，会占住那台机器（慢模型上一探就是一分钟）。
/// "太慢"那一类每 <b>2 分钟</b>探一次就够：它唯一的任务是认出"你换好了"，而探得太勤等于
/// 一边抱怨它慢、一边接着让它满负荷。
/// </description></item>
/// </list>
/// </summary>
public sealed class ModelAlertPacing
{
    /// <summary>要你去处理那类：外面的事，五分钟提一次够了。</summary>
    public static readonly TimeSpan NeedsAttentionRepeat = TimeSpan.FromMinutes(5);

    /// <summary>「服务没起来」和「慢到拿不到判定」：30 秒一次，见类注释。</summary>
    public static readonly TimeSpan UrgentRepeat = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 连不上那类的探测间隔：30 秒。这一类失败是**瞬间**的（连接被拒），探测几乎不花钱。
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
    /// 多久看一眼状态 —— <b>比 <see cref="UrgentRepeat"/> 细一档</b>。
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

    public static TimeSpan RepeatFor(ModelTroubleKind kind) =>
        kind == ModelTroubleKind.NeedsAttention ? NeedsAttentionRepeat : UrgentRepeat;

    public static TimeSpan ProbeEveryFor(ModelTroubleKind kind) =>
        kind == ModelTroubleKind.TooSlow ? SlowProbeEvery : CheapProbeEvery;

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

        if (Elapsed(_shown) < RepeatFor(kind))
            return PacingStep.Nothing;

        _shown = _now();
        return PacingStep.Alert;
    }

    /// <summary>距离上一次过了多久。没记过 = 无穷久（第一次一定到点）。</summary>
    private TimeSpan Elapsed(DateTimeOffset? at) =>
        at is null ? TimeSpan.MaxValue : _now() - at.Value;
}
