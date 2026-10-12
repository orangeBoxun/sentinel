using System.Windows;
using System.Windows.Threading;

namespace Sentinel.Agent;

/// <summary>
/// A full-screen notice that has to be dismissed. It covers whatever the user was doing, which is
/// the difference from a balloon: a balloon informs, this interrupts.
///
/// It still lets itself go after a while. Nothing that a self-control tool shows should be able to
/// strand someone in front of their own screen.
/// </summary>
public partial class AlertWindow : Window
{
    private const int AutoCloseSeconds = 20;

    private readonly DispatcherTimer _countdown;
    private int _secondsLeft;

    public AlertWindow()
    {
        InitializeComponent();

        _countdown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdown.Tick += (_, _) =>
        {
            _secondsLeft--;
            if (_secondsLeft <= 0)
            {
                Close();
                return;
            }

            UpdateCountdown();
        };
    }

    /// <summary>
    /// True once this window has been closed. A closed WPF window can never be shown again — calling
    /// Show() throws — so a reused instance must be checked for this, or the alert works exactly once
    /// and then fails silently forever.
    /// </summary>
    public bool IsGone { get; private set; }

    public void Show(string headline, string body)
    {
        Headline.Text = headline;
        Body.Text = body;

        _secondsLeft = AutoCloseSeconds;
        UpdateCountdown();

        // Maximize by hand rather than with WindowState: a borderless window with ResizeMode=NoResize
        // silently ignores WindowState=Maximized, and then it covers nothing.
        Show();
        WindowState = WindowState.Maximized;

        Activate();
        Topmost = true;
        _countdown.Start();
    }

    private void UpdateCountdown() => CountdownText.Text = $"{_secondsLeft} 秒后自动关闭";

    private void Dismiss_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        IsGone = true;
        _countdown.Stop();
        base.OnClosed(e);
    }
}
