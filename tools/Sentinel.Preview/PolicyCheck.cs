using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Sentinel.Agent;
using Sentinel.Core.Config;
using Sentinel.Core.Models;

namespace Sentinel.Preview;

/// <summary>
/// 改一个策略的提醒方式，看它到底有没有被存下来 —— 以及**什么时候**被存下来。
///
/// <para>
/// 起因是一个很具体的疑问：用户说选了「全屏提醒」但没生效，而磁盘上存的仍然是 Balloon。
/// 得先分清是"选项没写进去"还是"写进去了但没弹出全屏"—— 这两件事的修法完全不同。
/// </para>
/// <para>
/// 这里不启服务：Agent 找不到服务时会直接写文件，正好用来单独看"界面 → 配置"这一段。
/// </para>
/// </summary>
internal static class PolicyCheck
{
    public static int Run(string workDir)
    {
        // 这个检查要的是一个"没有服务"的环境。管道名不隔离的话，ServiceClient 会连上**真实的服务**,
        // 读到的、改的、存下的就都是用户的真配置 —— 我第一次跑正是这么把用户的 AlertStyle 写掉的。
        Environment.SetEnvironmentVariable("SENTINEL_PIPE", "Sentinel.Test.PolicyCheck.Unused");

        Directory.CreateDirectory(workDir);
        var configPath = Path.Combine(workDir, "config.json");
        WriteConfig(configPath);

        using var state = new AgentState(new[] { "--config", configPath });

        // 最后一道保险：确认手上这个配置确实是本检查自己写的那一份。
        // 光设管道名只是"希望"没人接，这一步才是"确认"。
        if (state.Config.Policies.Count != 1 || state.Config.Policies[0].Id != "p1")
        {
            Console.WriteLine("读到的不是本检查自己写的配置 —— 可能连上了别的服务，停手。");
            return 1;
        }

        var window = new MainWindow(state)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            ShowInTaskbar = false,
            ShowActivated = false,
            Width = 1180,
            Height = 800,
        };
        // 静默返回已经改成弹窗了：这个检查点按钮时不一定会选中条目，弹真框会把检查挂死。
        window.RefuseToUser = message => Console.WriteLine($"[被拒] {message.Split(char.MinValue)[0]}");


        window.Show();
        Pump();

        window.SelectTab(4);   // 策略
        Pump();

        if (window.FindName("PolicyList") is not ListBox list || list.Items.Count == 0)
        {
            Console.WriteLine("策略列表是空的。");
            return 1;
        }

        list.SelectedIndex = 0;
        Pump();

        var combo = (ComboBox)window.FindName("AlertStyleBox")!;
        Console.WriteLine($"打开时下拉框选的是 : {Describe(combo)}");
        Console.WriteLine($"磁盘上存的 AlertStyle: {OnDisk(configPath)}");
        Console.WriteLine();

        // 换成「全屏提醒」—— 和用户做的是同一个动作，仅此而已。
        var fullscreen = combo.Items.OfType<Choice<AlertStyle>>()
            .First(c => c.Value == AlertStyle.Fullscreen);
        combo.SelectedItem = fullscreen;
        Pump();

        Console.WriteLine($"下拉框现在选的是     : {Describe(combo)}");
        Console.WriteLine($"磁盘上存的 AlertStyle: {OnDisk(configPath)}   ← 还没点「应用修改」");
        Console.WriteLine();

        var apply = FindApplyButton(window);
        if (apply is null)
        {
            Console.WriteLine("找不到「应用修改」按钮。");
            return 1;
        }

        Console.WriteLine($"点的按钮     : 「{apply.Content}」（可用={apply.IsEnabled}）");
        apply.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Pump();

        Console.WriteLine("点过「应用修改」之后");
        Console.WriteLine($"  内存里的策略 AlertStyle: {state.Config.Policies.FirstOrDefault()?.AlertStyle}");
        Console.WriteLine($"  磁盘上存的 AlertStyle  : {OnDisk(configPath)}");
        Console.WriteLine($"  状态栏                 : {(window.FindName("SaveStatusText") as TextBlock)?.Text}");
        Console.WriteLine($"  校验行                 : {(window.FindName("ValidationText") as TextBlock)?.Text}");

        window.Close();
        return 0;
    }

    private static string Describe(ComboBox combo) =>
        combo.SelectedItem is Choice<AlertStyle> c ? $"{c.Display}（{c.Value}）" : "(没选)";

    private static string OnDisk(string configPath) =>
        ConfigStore.LoadOrDefault(configPath).Policies.FirstOrDefault()?.AlertStyle.ToString() ?? "(没有策略)";

    /// <summary>「应用修改」没有 x:Name，只能在表单里找那个写着它的按钮。</summary>
    private static Button? FindApplyButton(DependencyObject root)
    {
        if (root is Button b && b.Content is string text && text.Contains("应用修改"))
            return b;

        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var found = FindApplyButton(System.Windows.Media.VisualTreeHelper.GetChild(root, i));
            if (found is not null)
                return found;
        }

        return null;
    }

    private static void WriteConfig(string path)
    {
        var config = new AppConfig();
        config.Ui.DryRun = true;
        config.Logging.LogDirectory = Path.Combine(Path.GetDirectoryName(path)!, "logs");

        config.Policies.Add(new Policy
        {
            Id = "p1",
            Name = "只提醒并最小化",
            Actions = { ActionType.Notify, ActionType.Minimize },
            GraceSeconds = 3,
            CooldownSeconds = 30,
            DebounceCount = 1,
            AlertStyle = AlertStyle.Balloon,
        });

        ConfigStore.Save(config, path);
    }

    private static void Pump()
    {
        for (var i = 0; i < 4; i++)
            Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
    }
}
