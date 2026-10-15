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
    /// 本地服务没起来那类：**30 秒一次**。
    ///
    /// <para>
    /// 它和上面那类不一样：这是你自己的东西，拉起来就好（一条命令的事），而这期间
    /// 你照样能刷 —— 模型规则等于不存在，且没有任何别的迹象。所以要催得勤。
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
            return;
        }

        // **响之前先真问一次。** 那条停摆状态可能是陈旧的：它只在拿到判定时才被清掉，而判定要有人
        // 打开命中规则的页面才发起 —— 于是"昨晚模型没起来"留下的状态会一直挂着，提醒一直响，
        // 而它早就好了。探测通了就清掉状态、这次也不响。
        if (!_probing)
        {
            _probing = true;

            try
            {
                if (await supervisor!.ProbeAsync())
                {
                    _lastShown = DateTimeOffset.MinValue;
                    return;
                }
            }
            finally
            {
                _probing = false;
            }

            issue = supervisor!.BlockingIssue;

            if (issue is null)
            {
                _lastShown = DateTimeOffset.MinValue;
                return;
            }
        }

        var local = supervisor!.BlockingKind == ModelTroubleKind.LocalUnreachable;
        var repeat = local ? LocalRepeatEvery : RepeatEvery;

        if (DateTimeOffset.Now - _lastShown < repeat)
            return;

        _lastShown = DateTimeOffset.Now;

        _tray.ShowAlert(
            local ? "Sentinel · 本地模型没起来" : "Sentinel · 模型监督停了",
            local
                ? $"{issue}\n\n"
                  + "这是你自己机器上（或内网）的服务，拉起来就行 —— 比如把 Ollama 启动起来。\n"
                  + "在你拉起它之前，网站照样打得开，但「模型监督」的规则等于不存在 —— 没人判了。"
                : $"{issue}\n\n"
                  + "在修好之前，「模型监督」的规则不会生效 —— 它们现在等于不存在。\n"
                  + "去「大语言模型」页充值或者换一个服务商；不想用了就把「启用」取消掉（那算放宽，需要令牌）。",
            acted: true,
            AlertStyle.Fullscreen);
    }

    public void Dispose() => _timer.Stop();
}
