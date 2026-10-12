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
/// 「有未保存的修改」到底认不认得出来 —— 以及点完「应用修改」之后表单还在不在。
///
/// <para>
/// 起因是一句"配置没有保存是个什么鬼"。查下来保存失败是会弹窗的，真正的缺口是**改了但没点应用
/// 修改**：切页签、换条目，改动静悄悄地没了，而界面看起来你已经改好了。这个检查就是拿真窗口
/// 走一遍：改一个字段，看提示有没有出来；点应用，看提示有没有消掉、表单有没有被弄丢。
/// </para>
/// <para>
/// 不启服务：Agent 找不到服务时会直接写文件，正好用来单独看"界面 → 配置"这一段。管道名隔离
/// 加上配置文件隔离，绝不碰用户装好的那一套。
/// </para>
/// </summary>
internal static class DirtyCheck
{
    public static int Run(string workDir)
    {
        Environment.SetEnvironmentVariable("SENTINEL_PIPE", "Sentinel.Test.DirtyCheck.Unused");

        Directory.CreateDirectory(workDir);
        var configPath = Path.Combine(workDir, "config.json");
        WriteConfig(configPath);

        // 万一哪里弹了个模态框在等人点，别把检查挂死在那儿 —— 直接说清楚并退出。
        var watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        watchdog.Tick += (_, _) =>
        {
            Console.WriteLine();
            Console.WriteLine("超时：界面上多半弹了个对话框在等人点。");
            Environment.Exit(9);
        };
        watchdog.Start();

        using var state = new AgentState(new[] { "--config", configPath });

        if (state.Config.Targets.Count != 2 || state.Config.Targets[0].Id != "t1" || state.Config.Targets[1].Id != "t2")
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

        var targets = CheckTargetForm(window, state);
        var rules = CheckRuleForm(window, state);
        var leaving = CheckLeaving(window, state);

        window.Close();

        Console.WriteLine();
        var ok = targets && rules && leaving;
        Console.WriteLine(ok ? "结论：改动认得出来，离开也拦得住。" : "结论：有一处不对，见上面。");
        return ok ? 0 : 1;
    }

    /// <summary>
    /// 离开的时候拦不拦得住。三条分支：留下（取消）、放弃（否）、应用（是）。
    ///
    /// 这里把弹窗换成了自动回答 —— 一个真的模态框会把检查挂死，那等于这三条一条都没验过。
    /// </summary>
    private static bool CheckLeaving(MainWindow window, AgentState state)
    {
        Console.WriteLine("── 离开时拦一下 ──");

        var tabs = (TabControl)window.FindName("Tabs")!;
        var list = (ListBox)window.FindName("TargetList")!;
        var nameBox = (TextBox)window.FindName("TargetNameBox")!;

        const string pending = "没保存就走的改动";
        const string applied = "应用了再走";
        const string beforeSwitch = "换条目前的改动";

        // 让"回到目标页并选中第一个"变成一句话，省得中间状态互相干扰。
        void GoToFirstTarget()
        {
            window.SelectTab(2);
            Pump();
            list.SelectedIndex = 0;
            Pump();
            Settle();
        }

        bool ChangedTo(string value) => nameBox.Text == value;

        var ok = true;

        // 一、说「留下」：应该原地不动，连同那个没应用的改动。
        GoToFirstTarget();
        nameBox.Text = pending;
        Settle();
        window.AskLeaveDirtyForm = _ => MessageBoxResult.Cancel;
        tabs.SelectedIndex = 3;
        Pump();

        var stayed = tabs.SelectedIndex == 2 && ChangedTo(pending);
        Console.WriteLine($"说「留下」  : 还在目标页={tabs.SelectedIndex == 2}，改动还在={ChangedTo(pending)}");
        ok &= stayed;

        // 二、说「放弃」：走掉，改动不写盘。
        window.AskLeaveDirtyForm = _ => MessageBoxResult.No;
        tabs.SelectedIndex = 3;
        Pump();

        var left = tabs.SelectedIndex == 3 && OnDisk(state.ConfigPath).Targets[0].DisplayName != pending;
        Console.WriteLine($"说「放弃」  : 换到了规则页={tabs.SelectedIndex == 3}，磁盘上没被改={OnDisk(state.ConfigPath).Targets[0].DisplayName != pending}");
        ok &= left;

        // 三、说「应用」：走掉，而且改动写下去了。
        GoToFirstTarget();
        nameBox.Text = applied;
        Settle();
        window.AskLeaveDirtyForm = _ => MessageBoxResult.Yes;
        tabs.SelectedIndex = 3;
        Pump();

        var written = tabs.SelectedIndex == 3 && OnDisk(state.ConfigPath).Targets[0].DisplayName == applied;
        Console.WriteLine($"说「应用」  : 换到了规则页={tabs.SelectedIndex == 3}，磁盘上是「{OnDisk(state.ConfigPath).Targets[0].DisplayName}」");
        ok &= written;

        // 四、在列表里换条目也该拦：说「留下」的话，选中项要拨回去。
        GoToFirstTarget();
        nameBox.Text = beforeSwitch;
        Settle();
        window.AskLeaveDirtyForm = _ => MessageBoxResult.Cancel;
        list.SelectedIndex = 1;
        Pump();

        var reverted = list.SelectedIndex == 0 && ChangedTo(beforeSwitch);
        Console.WriteLine($"换条目说「留下」: 选中项拨回去了={list.SelectedIndex == 0}，改动还在={ChangedTo(beforeSwitch)}");
        ok &= reverted;

        window.AskLeaveDirtyForm = _ => MessageBoxResult.Yes;
        Console.WriteLine();
        return ok;
    }

    private static bool CheckTargetForm(MainWindow window, AgentState state)
    {
        Console.WriteLine("── 目标表单 ──");
        window.SelectTab(2);
        Pump();

        if (window.FindName("TargetList") is not ListBox list || list.Items.Count == 0)
        {
            Console.WriteLine("目标列表是空的。");
            return false;
        }

        list.SelectedIndex = 0;
        Pump();

        var mark = (TextBlock)window.FindName("TargetDirtyMark")!;
        var button = (Button)window.FindName("ApplyTargetButton")!;
        var form = (FrameworkElement)window.FindName("TargetForm")!;

        Console.WriteLine($"刚选中      : 提示={Show(mark)}  按钮={StyleName(window, button)}");

        var clean = mark.Visibility == Visibility.Collapsed;

        // 改一个字段 —— 和用户做的是同一个动作。
        ((TextBox)window.FindName("TargetNameBox")!).Text = "改过的名字";
        Settle();

        Console.WriteLine($"改了一个字段: 提示={Show(mark)}  按钮={StyleName(window, button)}");
        var dirty = mark.Visibility == Visibility.Visible;

        Console.WriteLine($"磁盘上还是  : {OnDisk(configPath: state.ConfigPath).Targets[0].DisplayName}   ← 还没点应用");

        button.RaiseEvent(new RoutedEventArgs(ButtonClick.Event));
        Pump();

        Console.WriteLine($"点过应用修改: 提示={Show(mark)}  按钮={StyleName(window, button)}");
        Console.WriteLine($"磁盘上是    : {OnDisk(state.ConfigPath).Targets[0].DisplayName}");
        Console.WriteLine($"表单还在吗  : 选中项={list.SelectedIndex}  表单可用={form.IsEnabled}");

        var applied = mark.Visibility == Visibility.Collapsed
                      && OnDisk(state.ConfigPath).Targets[0].DisplayName == "改过的名字"
                      && list.SelectedIndex == 0
                      && form.IsEnabled;

        Console.WriteLine();
        return clean && dirty && applied;
    }

    private static bool CheckRuleForm(MainWindow window, AgentState state)
    {
        Console.WriteLine("── 规则表单 ──");
        window.SelectTab(3);
        Pump();

        if (window.FindName("RuleList") is not ListBox list || list.Items.Count == 0)
        {
            Console.WriteLine("规则列表是空的。");
            return false;
        }

        list.SelectedIndex = 0;
        Pump();

        var mark = (TextBlock)window.FindName("RuleDirtyMark")!;
        var button = (Button)window.FindName("ApplyRuleButton")!;
        var form = (FrameworkElement)window.FindName("RuleForm")!;

        Console.WriteLine($"刚选中      : 提示={Show(mark)}");
        var clean = mark.Visibility == Visibility.Collapsed;

        ((TextBox)window.FindName("RuleNameBox")!).Text = "改过的规则名";
        Settle();

        Console.WriteLine($"改了一个字段: 提示={Show(mark)}  按钮={StyleName(window, button)}");
        var dirty = mark.Visibility == Visibility.Visible;

        Console.WriteLine($"  点之前: 规则列表={list.Items.Count} 项，选中={list.SelectedIndex}，" +
                          $"目标选择器={((ComboBox)window.FindName("RuleTargetPicker")!).SelectedIndex}");

        button.RaiseEvent(new RoutedEventArgs(ButtonClick.Event));
        Pump();

        Console.WriteLine($"  点之后: 规则列表={list.Items.Count} 项，选中={list.SelectedIndex}，" +
                          $"目标选择器={((ComboBox)window.FindName("RuleTargetPicker")!).SelectedIndex}");

        var onDisk = OnDisk(state.ConfigPath).Targets[0].Rules[0].Name;
        Console.WriteLine($"点过应用修改: 提示={Show(mark)}");
        Console.WriteLine($"磁盘上是    : {onDisk}");
        Console.WriteLine($"表单还在吗  : 选中项={list.SelectedIndex}  表单可用={form.IsEnabled}");

        var applied = mark.Visibility == Visibility.Collapsed
                      && onDisk == "改过的规则名"
                      && list.SelectedIndex == 0
                      && form.IsEnabled;

        Console.WriteLine();

        // ---- 多行「匹配内容」会不会被吞掉
        //
        // 用户报过这个："界面上明明写了 7 行，存下去只有 2 行 —— 所以我配的 http 不起作用。"
        // 直接验它：往那个框里打 7 行、点应用，然后去文件里数。
        var wanted = new[] { "bilibili", "douyin", "http", "a", "b", "c", "d" };

        ((TextBox)window.FindName("RulePatternsBox")!).Text = string.Join(Environment.NewLine, wanted);
        Settle();
        button.RaiseEvent(new RoutedEventArgs(ButtonClick.Event));
        Pump();

        var saved = OnDisk(state.ConfigPath).Targets[0].Rules[0].Match.Patterns;

        Console.WriteLine($"打了 {wanted.Length} 行、点应用之后，文件里有 {saved.Count} 条：{string.Join(" / ", saved)}");

        var kept = saved.Count == wanted.Length && wanted.All(saved.Contains);

        Console.WriteLine();
        return clean && dirty && applied && kept;
    }

    private static string Show(TextBlock mark) =>
        mark.Visibility == Visibility.Visible ? "有" : "没有";

    private static string StyleName(Window window, Button button) =>
        ReferenceEquals(button.Style, window.FindResource("PrimaryIconButton")) ? "绿的（可以点）"
        : ReferenceEquals(button.Style, window.FindResource("IconButton")) ? "灰的（没改动）"
        : "?";

    private static AppConfig OnDisk(string configPath) => ConfigStore.LoadOrDefault(configPath);

    private static void WriteConfig(string path)
    {
        var config = new AppConfig();
        config.Ui.DryRun = true;
        config.Logging.LogDirectory = Path.Combine(Path.GetDirectoryName(path)!, "logs");

        var target = new Target { Id = "t1", DisplayName = "Codex", Reader = new ReaderOptions { IntervalMs = 1000 } };
        target.Rules.Add(new Rule { Id = "r1", Name = "旧规则名" });
        config.Targets.Add(target);

        // 第二个目标只是为了能试"在列表里换一个条目"这条路径。
        config.Targets.Add(new Target { Id = "t2", DisplayName = "豆包工作", Reader = new ReaderOptions { IntervalMs = 1000 } });

        config.Policies.Add(new Policy { Id = "p1", Name = "只提醒", Actions = { ActionType.Notify } });

        ConfigStore.Save(config, path);
    }

    private static void Pump()
    {
        for (var i = 0; i < 4; i++)
            Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
    }

    /// <summary>
    /// 等到提示真的刷新出来。
    ///
    /// 那一行是跟着状态栏每秒刷一次走的（不挂在每个输入框的 Change 上 —— 那样覆盖不到"程序自己
    /// 改了控件"的情形）。所以这里得等过那一刻，不能只把消息队列抽干。
    /// </summary>
    private static void Settle()
    {
        var end = DateTime.UtcNow.AddSeconds(1.5);
        while (DateTime.UtcNow < end)
        {
            Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            Thread.Sleep(50);
        }
    }

    /// <summary>ButtonBase.ClickEvent 的那个路由事件，写短一点。</summary>
    private static class ButtonClick
    {
        public static readonly RoutedEvent Event = ButtonBase.ClickEvent;
    }
}
