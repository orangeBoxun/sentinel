using System.Drawing;
using Sentinel.Core.Models;
using WinForms = System.Windows.Forms;

namespace Sentinel.Agent;

/// <summary>
/// The tray presence: an icon by the clock, a context menu, and the two ways the monitor tells the
/// user something — a balloon, or a full-screen notice. There is no main window until the user asks
/// for one, which is what makes this a background monitor rather than a window app that happens to
/// stay open.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly AgentState _state;
    private readonly WinForms.ToolStripMenuItem _exit;
    private MainWindow? _window;
    private AlertWindow? _alert;

    public TrayIcon(AgentState state)
    {
        _state = state;

        _icon = new WinForms.NotifyIcon
        {
            Icon = AppIcon.ForTray(),
            Text = "Sentinel",
            Visible = true,
        };

        var menu = new WinForms.ContextMenuStrip { ShowItemToolTips = true };
        menu.Items.Add("打开设置", null, (_, _) => ShowSettings());
        menu.Items.Add("密码保险箱", null, (_, _) => ShowVault());
        menu.Items.Add("查看日志", null, (_, _) => ShowSettings(MainWindow.LogTabIndex));
        menu.Items.Add(new WinForms.ToolStripSeparator());

        _exit = new WinForms.ToolStripMenuItem("退出", null, (_, _) => ExitApplication());
        menu.Items.Add(_exit);

        // Worked out as the menu opens rather than once at startup: unlock protection can be switched
        // on while this Agent is running, and the item has to be right the next time it is looked at.
        menu.Opening += (_, _) => RefreshExitItem();

        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => ShowSettings();
    }

    /// <summary>
    /// Greys out 退出 while unlock protection is on.
    ///
    /// <para>
    /// Not because quitting would get anyone anywhere — the Service brings the Agent straight back —
    /// but because a menu that offers to switch monitoring off is teaching the wrong thing. If the
    /// Agent genuinely needs restarting, that can be done from Task Manager, which is a place this
    /// program does not put in front of anyone.
    /// </para>
    /// </summary>
    private void RefreshExitItem()
    {
        var locked = _state.UnlockConfigured;

        _exit.Enabled = !locked;
        _exit.ToolTipText = locked ? "解锁保护已开启，所以这里退不了。" : "";
    }

    private void ShowVault() => Vault.Show(_state);

    /// <summary>
    /// Delivers a notice the way its policy asked for. A full-screen overlay covers whatever the user
    /// was doing until they acknowledge it — that interruption is the feature, not the cost.
    /// </summary>
    public void ShowAlert(string title, string message, bool acted, AlertStyle style)
    {
        if (style != AlertStyle.Fullscreen)
        {
            ShowBalloon(title, message, acted);
            return;
        }

        // Deliberately not caught here. A notice that silently fails to appear is the worst possible
        // outcome for this program: the user believes they were warned. Let the monitor record it.
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
            throw new InvalidOperationException("no UI thread available to show the alert");

        dispatcher.Invoke(() =>
        {
            // A window that has been closed cannot be shown again.
            if (_alert is null || _alert.IsGone)
                _alert = new AlertWindow();

            _alert.Show(title, message);
        });
    }

    /// <summary>
    /// Shows a notice. <paramref name="acted"/> picks the icon: a warning for the grace-period
    /// "about to block", an error for "blocked". The two fire seconds apart for the same hit, so an
    /// identical look makes one event read as two.
    /// </summary>
    public void ShowBalloon(string title, string message, bool acted)
    {
        try
        {
            _icon.BalloonTipTitle = title;
            _icon.BalloonTipText = message;
            _icon.BalloonTipIcon = acted
                ? WinForms.ToolTipIcon.Error
                : WinForms.ToolTipIcon.Warning;
            _icon.ShowBalloonTip(5000);
        }
        catch
        {
            // A failed balloon must never take down the monitor.
        }
    }

    public void ShowSettings(int tabIndex = 0)
    {
        if (_window is null)
        {
            _window = new MainWindow(_state);
            _window.Closed += (_, _) => _window = null;
            _window.Show();
        }
        else
        {
            _window.Activate();
        }

        _window.SelectTab(tabIndex);
        _window.WindowState = System.Windows.WindowState.Normal;
    }

    private void ExitApplication()
    {
        // Reached only when unlock protection is off — the menu item is disabled while it is on — so
        // there is nothing to explain and nothing to warn about. The Agent says goodbye on the way
        // out and the Service brings the next one up in seconds.
        _state.Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    public void Dispose()
    {
        _icon.Visible = false;

        // NotifyIcon does not own the icon it was handed, so it outlives the control unless it is
        // released here.
        var icon = _icon.Icon;
        _icon.Dispose();
        icon?.Dispose();
    }
}
