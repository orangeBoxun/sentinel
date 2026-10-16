using System.Windows.Threading;
using Sentinel.Core.Model;
using Sentinel.Core.Models;

namespace Sentinel.Agent;

/// <summary>
/// 模型监督停摆时，用全屏提醒反复说 —— 直到它真的好了。
///
/// <para>
/// 为什么非要吵这个：模型判不出来的时候规则会**先放行**（那是对的，等待会把整个监控卡住）。
/// 但代价是——**那几条规则现在等于不存在**，而界面上一切照旧：目标还在、规则还在、监控也显示
/// 在运行。不提醒的话，"模型监督"会悄悄地不再监督任何东西，而你以为它在。
/// </para>
/// <para>
/// 只对"要你去处理"的那类和"你自己的服务"那类提醒：钥匙不对、欠费、超额、本地模型没起来、
/// 模型慢到拿不到判定。**公网服务连不上不提醒** —— 那时候网站自己也打不开，拦不拦没有区别，
/// 为一件不会发生的事弹全屏只会让人把这个功能关掉。
/// </para>
/// <para>
/// 节奏（多久催一次、多久探一次）在 <see cref="ModelAlertPacing"/> 里，那里能测；这里只负责
/// 取状态、弹提醒。
/// </para>
/// </summary>
internal sealed class ModelAlertWatcher : IDisposable
{
    private readonly AgentState _state;
    private readonly TrayIcon _tray;
    private readonly DispatcherTimer _timer;
    private readonly ModelAlertPacing _pacing = new();

    public ModelAlertWatcher(AgentState state, TrayIcon tray)
    {
        _state = state;
        _tray = tray;

        _timer = new DispatcherTimer { Interval = ModelAlertPacing.CheckEvery };
        _timer.Tick += async (_, _) => await CheckAsync();
        _timer.Start();
    }

    private async Task CheckAsync()
    {
        var supervisor = _state.Monitor.ModelSupervisor;

        if (supervisor?.BlockingIssue is null)
        {
            // 修好了。下次再出问题要立刻提醒，而不是接着上一次的计时。
            _pacing.Reset();
            return;
        }

        // **响之前先真问一次。** 那条停摆状态可能是陈旧的：它只在拿到判定时才被清掉，而判定要有人
        // 打开命中规则的页面才发起 —— 于是"昨晚模型没起来"留下的状态会一直挂着，提醒一直响，
        // 而它早就好了。探测通了就清掉状态、这次也不响。
        //
        // 探测问的是**和真实判定同一句话、同样大小的正文**，所以"探测答得上来了"确实等于
        // "真实判定也答得上来了"。这一点错了的话，提醒会在一段时间里完全停掉 —— 见
        // ModelSupervisor.ProbeInstruction。
        var step = await _pacing.StepAsync(supervisor.BlockingKind, supervisor.ProbeAsync);

        if (step != PacingStep.Alert)
            return;

        var kind = supervisor.BlockingKind;
        var issue = supervisor.BlockingIssue;

        // 探测（或者别的线程上刚好回来的那次判定）可能刚刚把它清掉 —— 那就没什么可说的了。
        if (issue is null)
            return;

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
