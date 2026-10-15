using System.Windows.Threading;
using Sentinel.Core.Model;
using Sentinel.Core.Models;

namespace Sentinel.Agent;

/// <summary>
/// 模型监督停摆时，每 5 分钟用全屏提醒说一次。
///
/// <para>
/// 为什么非要吵这个：模型判不出来的时候规则会**先放行**（那是对的，等待会把整个监控卡住）。
/// 但代价是——**那几条规则现在等于不存在**，而界面上一切照旧：目标还在、规则还在、监控也显示
/// 在运行。不提醒的话，"模型监督"会悄悄地不再监督任何东西，而你以为它在。
/// </para>
/// <para>
/// 只对"要你去处理"的那类失败提醒：钥匙不对、欠费、超额。**没网不提醒** —— 没网的时候
/// 那些网站自己也打不开，拦不拦没有区别，为一件不会发生的事弹全屏只会让人把这个功能关掉。
/// </para>
/// </summary>
internal sealed class ModelAlertWatcher : IDisposable
{
    /// <summary>要你去处理那类（钥匙、欠费）的节奏：外面的事，五分钟提一次够了。</summary>
    private static readonly TimeSpan RepeatEvery = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 「你自己的服务没起来」和「慢到拿不到判定」这两类：**30 秒一次**。
    ///
    /// <para>
    /// 它们和上面那类不一样：模型规则等于不存在，而你照样能刷，界面上没有任何别的迹象。
    /// 两件都是你一分钟内能办的事（拉起来、或者换个模型），所以要催得勤 ——
    /// 不然"切一下本地慢模型"就等于悄悄把模型监督关掉了。
    /// </para>
    /// </summary>
    private static readonly TimeSpan LocalRepeatEvery = TimeSpan.FromSeconds(30);

    /// <summary>多久看一眼状态。问题什么时候出现是不可预知的，所以查得比提醒本身勤。</summary>
    private static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(30);

    private readonly AgentState _state;
    private readonly TrayIcon _tray;
    private readonly DispatcherTimer _timer;

    private DateTimeOffset _lastShown = DateTimeOffset.MinValue;

    /// <summary>探测进行中就不重复发 —— 一次请求的时间可能比定时器间隔还长。</summary>
    private bool _probing;

    /// <summary>上一次探测的时刻。</summary>
    private DateTimeOffset _lastProbed = DateTimeOffset.MinValue;

    /// <summary>
    /// 「太慢」那类的探测间隔：**90 秒**。
    ///
    /// <para>
    /// 比提醒的 30 秒稀，因为探测本身要花模型的时间（在慢模型上一次就是几十秒）——
    /// 提醒是免费的，探测不是。90 秒足够在"换了模型"之后两分钟内自动恢复，
    /// 又不至于让探测自己把机器拖住。
    /// </para>
    /// </summary>
    private static readonly TimeSpan SlowProbeEvery = TimeSpan.FromSeconds(90);

    public ModelAlertWatcher(AgentState state, TrayIcon tray)
    {
        _state = state;
        _tray = tray;

        _timer = new DispatcherTimer { Interval = PollEvery };
        _timer.Tick += async (_, _) => await CheckAsync();
        _timer.Start();
    }

    private async Task CheckAsync()
    {
        var supervisor = _state.Monitor.ModelSupervisor;
        var issue = supervisor?.BlockingIssue;

        if (issue is null)
        {
            // 修好了。下次再出问题要立刻提醒，而不是接着上一次的计时。
            _lastShown = DateTimeOffset.MinValue;
            _lastProbed = DateTimeOffset.MinValue;
            return;
        }

        // **响之前先真问一次。** 那条停摆状态可能是陈旧的：它只在拿到判定时才被清掉，而判定要有人
        // 打开命中规则的页面才发起 —— 于是"昨晚模型没起来"留下的状态会一直挂着，提醒一直响，
        // 而它早就好了。探测通了就清掉状态、这次也不响。
        var probeEvery = supervisor!.BlockingKind == ModelTroubleKind.TooSlow
            ? SlowProbeEvery
            : PollEvery;   // 连不上那类：探测是瞬时失败，随便探

        if (!_probing && DateTimeOffset.Now - _lastProbed >= probeEvery)
        {
            _lastProbed = DateTimeOffset.Now;
            _probing = true;

            try
            {
                if (await supervisor.ProbeAsync())
                {
                    _lastShown = DateTimeOffset.MinValue;
                    _lastProbed = DateTimeOffset.MinValue;
                    return;
                }
            }
            finally
            {
                _probing = false;
            }

            issue = supervisor.BlockingIssue;

            if (issue is null)
            {
                _lastShown = DateTimeOffset.MinValue;
                return;
            }
        }

        var kind = supervisor!.BlockingKind;
        var urgent = kind is ModelTroubleKind.LocalUnreachable or ModelTroubleKind.TooSlow;
        var repeat = urgent ? LocalRepeatEvery : RepeatEvery;

        if (DateTimeOffset.Now - _lastShown < repeat)
            return;

        _lastShown = DateTimeOffset.Now;

        var title = kind switch
        {
            ModelTroubleKind.LocalUnreachable => "Sentinel · 本地模型没起来",
            ModelTroubleKind.TooSlow => "Sentinel · 模型太慢，判定拿不到",
            _ => "Sentinel · 模型监督停了",
        };

        var body = kind switch
        {
            ModelTroubleKind.LocalUnreachable =>
                $"{issue}\n\n"
                + "这是你自己机器上（或内网）的服务，拉起来就行 —— 比如把 Ollama 启动起来。\n"
                + "在你拉起它之前，网站照样打得开，但「模型监督」的规则等于不存在 —— 没人判了。",

            ModelTroubleKind.TooSlow =>
                $"{issue}\n\n"
                + "**换一个小一点的模型，或者换一个服务商** —— 慢到拿不到判定，和没有判定是一回事："
                + "那几条规则现在等于不存在，而你照样能刷。\n"
                + "本机跑大模型最容易这样；在「大语言模型」页把模型名换小一档（比如 7B 换成 3B），"
                + "或者启用一个在线服务商。",

            _ =>
                $"{issue}\n\n"
                + "在修好之前，「模型监督」的规则不会生效 —— 它们现在等于不存在。\n"
                + "去「大语言模型」页充值或者换一个服务商；不想用了就把「启用」取消掉（那算放宽，需要令牌）。",
        };

        _tray.ShowAlert(title, body, acted: true, AlertStyle.Fullscreen);
    }

    public void Dispose() => _timer.Stop();
}
