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
/// 按一次诊断页的「测一次模型监督」，把那份报告原样打出来。
///
/// <para>
/// 这是给"配了模型监督却什么也没发生"用的：那条链上任何一处错位，表现都一模一样，
/// 所以只能一步一步看。这个检查就是确认那份报告**真的能指出问题**（而不是又给一句
/// "不知道哪里的问题"）。
/// </para>
/// <para>
/// 它打的是**真实的窗口**（你现在屏幕上那个 Edge）和**你配置里那个服务商** ——
/// 也就是说，它会真的发一次请求。隔离环境：自己的管道名和配置，不碰你装好的那一套。
/// </para>
/// </summary>
internal static class DiagCheck
{
    public static int Run(string workDir)
    {
        Environment.SetEnvironmentVariable("SENTINEL_PIPE", $"Sentinel.Test.DiagCheck.{Environment.ProcessId}");

        Directory.CreateDirectory(workDir);
        var configPath = Path.Combine(workDir, "config.json");

        if (!WriteConfig(configPath, out var note))
        {
            Console.WriteLine(note);
            return 1;
        }

        Console.WriteLine(note);
        Console.WriteLine();

        using var state = new AgentState(new[] { "--config", configPath });

        var window = new MainWindow(state);
        Offscreen(window, 1280, 900);        // 静默返回已经改成弹窗了：这个检查点按钮时不一定会选中条目，弹真框会把检查挂死。
        window.RefuseToUser = message => Console.WriteLine($"[被拒] {message.Split(char.MinValue)[0]}");


        window.Show();
        Pump();

        // 诊断页。切过去的时候它自己会去枚举窗口。
        window.SelectTab(8);
        Pump();

        if (window.FindName("DiagModelTargetPicker") is not ComboBox picker)
        {
            Console.WriteLine("找不到目标下拉框 —— 这个检查要跟着界面一起改。");
            return 1;
        }

        picker.SelectedIndex = 0;
        Pump();

        var button = (Button)window.FindName("DiagModelButton")!;
        var box = (TextBox)window.FindName("DiagModelBox")!;

        Console.WriteLine($"点「{button.Content}」…（它要读一次真实窗口、再问一次模型，慢几秒）");
        Console.WriteLine();

        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

        // 异步的：等那一段"正在查…"被真正的报告替掉。
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

            if (box.Text.Length > 0 && !box.Text.StartsWith("正在查", StringComparison.Ordinal))
                break;

            Thread.Sleep(150);
        }

        Console.WriteLine("──────── 报告原文 ────────");
        Console.WriteLine(box.Text);
        Console.WriteLine("──────────────────────────");

        var usable = box.Text.Contains("一步步看到的东西")
                     && (box.Text.Contains("发现的问题") || box.Text.Contains("没有发现断点"));

        // ---- 回归：加进「模型监督自检」之后，上面那张卡的正文框被挤成了一条缝 ——
        // 「测试读取」其实跑了，结果显示在那条缝里，看着就像点了没反应。这两条一起验。
        var bodyBox = (TextBox)window.FindName("DiagBodyBox")!;
        var verdict = (TextBlock)window.FindName("DiagVerdictText")!;
        var windowPicker = (ComboBox)window.FindName("DiagWindowPicker")!;

        Console.WriteLine();
        Console.WriteLine($"上面那张卡的正文框高度: {bodyBox.ActualHeight:0} 像素（被挤掉的话这里会很小）");

        var roomy = bodyBox.ActualHeight >= 150;

        var readWorks = false;

        if (windowPicker.Items.Count > 0)
        {
            windowPicker.SelectedIndex = 0;
            Pump();

            // 模型自检**之后**，普通的读取测试还得能用 —— 而且结果得看得见。
            FindInTab(window, "测试读取")?.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();

            readWorks = verdict.Text.Length > 0;
            Console.WriteLine($"模型自检之后再点「测试读取」: {(readWorks ? verdict.Text : "(什么都没出来)")}");
        }
        else
        {
            Console.WriteLine("窗口列表是空的，跳过「测试读取」那一项。");
            readWorks = true;
        }

        window.Close();

        var ok = usable && roomy && readWorks;

        Console.WriteLine();
        Console.WriteLine(ok
            ? "结论：报告出得来、列到了具体步骤，而且两块都还用得了。"
            : "结论：有一样不对 —— 报告 / 正文框高度 / 读取测试，见上面。");

        return ok ? 0 : 1;
    }

    /// <summary>在当前选中的页签里按文字找按钮（「新建」那种文字在多个页签里都有，不能全窗口搜）。</summary>
    private static ButtonBase? FindInTab(Window window, string text)
    {
        if (window.FindName("Tabs") is not TabControl tabs)
            return null;

        return Find(tabs.SelectedContent as DependencyObject, text);
    }

    private static ButtonBase? Find(DependencyObject? root, string text)
    {
        if (root is null)
            return null;

        if (root is ButtonBase b && b.Content is string s && s == text)
            return b;

        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var found = Find(System.Windows.Media.VisualTreeHelper.GetChild(root, i), text);
            if (found is not null)
                return found;
        }

        return null;
    }

    private static bool WriteConfig(string path, out string note)
    {
        // 服务商从你装好的那份配置里取（只读，不打印 key）—— 这样测的就是你手上这一套。
        var realPath = @"E:\Sentinel\config.json";

        if (!File.Exists(realPath)
            || !ConfigStore.TryLoad(realPath, out var installed, out _)
            || installed.Models.FirstOrDefault(m => m.Id == installed.ActiveModelId) is not { } active)
        {
            note = $"读不到你配置里的服务商（{realPath}）—— 先配一个再跑这个检查。";
            return false;
        }

        var config = new AppConfig();
        config.Ui.DryRun = true;
        config.Logging.LogDirectory = Path.Combine(Path.GetDirectoryName(path)!, "logs");

        config.Models.Add(active);
        config.ActiveModelId = active.Id;

        config.Policies.Add(new Policy
        {
            Id = "p",
            Name = "提醒并最小化",
            Actions = { ActionType.Notify, ActionType.Minimize },
            GraceSeconds = 0,
            CooldownSeconds = 0,
            DebounceCount = 1,
        });

        // 目标：Edge。正文开着 —— 自检要能看出"没读正文"和"读了正文"的区别。
        config.Targets.Add(new Target
        {
            Id = "edge",
            DisplayName = "Edge（自检用）",
            ProcessNames = { "msedge" },
            Reader = new ReaderOptions { ScanUrl = true, ScanTitle = true, ScanBody = true, IntervalMs = 2000 },
            Rules =
            {
                new Rule
                {
                    Id = "r",
                    Name = "模型监督：游戏页",
                    Enabled = true,
                    PolicyId = "p",
                    Match = new MatchSpec
                    {
                        Type = MatchKind.Model,
                        Fields = { MatchField.Url, MatchField.Body },
                        Patterns = { "和游戏有关的页面，尤其是攻略、排行榜和搜索页" },
                    },
                },
            },
        });

        ConfigStore.Save(config, path);

        note = $"用你配置里的服务商：{active.Name} / {active.Model}（key 不打印）";
        return true;
    }

    private static void Offscreen(Window window, int width, int height)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -20000;
        window.Top = -20000;
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        window.Width = width;
        window.Height = height;
    }

    private static void Pump()
    {
        for (var i = 0; i < 4; i++)
            Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
    }
}
