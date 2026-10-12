using System.Threading;
using System.Windows;

namespace Sentinel.Agent;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private TrayIcon? _tray;
    private AgentState? _state;
    private ModelAlertWatcher? _modelWatch;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var startedByService = e.Args.Contains("--service", StringComparer.OrdinalIgnoreCase);

        // Two Agents would double every notification and race on the config file.
        _singleInstance = new Mutex(initiallyOwned: true, @"Local\Sentinel.Agent", out var isFirst);
        if (!isFirst)
        {
            // The service re-checks periodically and may launch us while an instance is still
            // starting up; a dialog there would be pointless noise on someone's desktop.
            if (!startedByService)
            {
                MessageBox.Show("Sentinel 已经在运行了（看右下角托盘区）。", "Sentinel",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }

            Shutdown();
            return;
        }

        var state = new AgentState(e.Args);
        _state = state;
        _tray = new TrayIcon(state);

        // Route notifications through the Agent's own tray icon instead of letting the monitor
        // create a second icon.
        state.Monitor.Balloon = _tray.ShowAlert;

        // 模型监督停摆时每 5 分钟吵一次 —— 否则它会悄悄地不再监督任何东西，而界面上看不出区别。
        _modelWatch = new ModelAlertWatcher(state, _tray);

        state.Start();

        if (state.OpenSettingsOnStart)
            _tray.ShowSettings(Sentinel.Agent.MainWindow.OverviewTab);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _modelWatch?.Dispose();
        _tray?.Dispose();

        // The mutex goes first. Otherwise the replacement the Service starts is greeted by this
        // copy still holding it, exits itself, and the session looks alive for another timeout.
        _singleInstance?.Dispose();

        // Best effort — the process is shutting down. If it does not get through, the Service falls
        // back to its heartbeat timeout, which is what would have happened anyway.
        try
        {
            _state?.SayGoodbye();
        }
        catch
        {
            // Nothing useful to do while exiting.
        }

        base.OnExit(e);
    }
}
