using System.Windows.Threading;
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
    /// <summary>用户点名要的节奏。</summary>
    private static readonly TimeSpan RepeatEvery = TimeSpan.FromMinutes(5);

    /// <summary>多久看一眼状态。问题什么时候出现是不可预知的，所以查得比提醒本身勤。</summary>
    private static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(30);

    private readonly AgentState _state;
    private readonly TrayIcon _tray;
    private readonly DispatcherTimer _timer;

    private DateTimeOffset _lastShown = DateTimeOffset.MinValue;

    public ModelAlertWatcher(AgentState state, TrayIcon tray)
    {
        _state = state;
        _tray = tray;

        _timer = new DispatcherTimer { Interval = PollEvery };
        _timer.Tick += (_, _) => Check();
        _timer.Start();
    }

    private void Check()
    {
        var issue = _state.Monitor.ModelSupervisor?.BlockingIssue;

        if (issue is null)
        {
            // 修好了。下次再出问题要立刻提醒，而不是接着上一次的计时。
            _lastShown = DateTimeOffset.MinValue;
            return;
        }

        if (DateTimeOffset.Now - _lastShown < RepeatEvery)
            return;

        _lastShown = DateTimeOffset.Now;

        _tray.ShowAlert(
            "Sentinel · 模型监督停了",
            $"{issue}\n\n"
            + "在修好之前，「模型监督」的规则不会生效 —— 它们现在等于不存在。\n"
            + "去「大语言模型」页充值或者换一个服务商；不想用了就把「启用」取消掉（那算放宽，需要令牌）。",
            acted: true,
            AlertStyle.Fullscreen);
    }

    public void Dispose() => _timer.Stop();
}
