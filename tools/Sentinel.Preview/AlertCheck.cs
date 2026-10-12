using System.Windows;
using System.Windows.Threading;
using Sentinel.Agent;

namespace Sentinel.Preview;

/// <summary>
/// 把全屏提醒真的弹一次，量一下它到底盖没盖住屏幕。
///
/// <para>
/// 起因：用户说选了「全屏提醒」但没看到全屏。查下来有两件事，得分开验：
/// 一是那个选择有没有存进配置（另一个检查负责），二是这个窗口弹出来时是不是真的全屏。
/// 后者只能真的弹一次才知道 —— AlertWindow 的注释里写着"无边框 + NoResize 会让
/// WindowState=Maximized 被静默忽略，那就什么都没盖住"，而这种失败是**看不见的**：
/// 窗口出现了，只是很小。
/// </para>
/// </summary>
internal static class AlertCheck
{
    public static int Run()
    {
        var window = new AlertWindow();

        // 弹一下，量完立刻关。整个过程不到一秒。
        window.Show("Sentinel · 全屏提醒自检", "这条提醒只是为了确认全屏窗口真的盖住了屏幕，马上就会关掉。");
        Pump();

        var work = SystemParameters.WorkArea;

        Console.WriteLine($"窗口可见    : {window.IsVisible}");
        Console.WriteLine($"窗口状态    : {window.WindowState}");
        Console.WriteLine($"实际尺寸    : {window.ActualWidth:0} x {window.ActualHeight:0}");
        Console.WriteLine($"屏幕工作区  : {work.Width:0} x {work.Height:0}");
        Console.WriteLine($"整屏        : {SystemParameters.PrimaryScreenWidth:0} x {SystemParameters.PrimaryScreenHeight:0}");
        Console.WriteLine($"置顶        : {window.Topmost}");

        var covers = window.ActualWidth >= work.Width - 1 && window.ActualHeight >= work.Height - 1;
        Console.WriteLine($"盖住工作区  : {covers}");

        window.Close();
        Pump();

        return covers ? 0 : 1;
    }

    private static void Pump()
    {
        for (var i = 0; i < 4; i++)
            Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
    }
}
