using System.Diagnostics;
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
/// 复现"配置里有半成品服务商 → 什么编辑都存不下去"，并确认修好之后能存。
///
/// <para>
/// 起因是用户按不了「新建规则」：一点就弹"配置没有保存"。根因不是规则，是配置里有两个
/// 没填完的服务商，而校验把"没填地址"判成 Error，保存时只要有一个 Error 就整次拒绝 ——
/// 于是加不了规则、也删不掉那两个服务商（删除同样要保存，而保存时它们还在）。一个中间状态
/// 把整个配置锁死了。
/// </para>
/// <para>
/// 跑在隔离环境里：自己的管道名、临时目录里的配置。不碰用户装好的那一套。
/// </para>
/// </summary>
internal static class SaveCheck
{
    /// <summary>
    /// 每次运行换一个管道名。
    ///
    /// 固定的名字有个坑：上一次跑挂掉的测试服务还占着它，这一次的服务就绑不上，
    /// 而等待方会连上**上一个**服务 —— 于是检查测的是上一次的进程，还会一直卡住。
    /// 第一次写这个检查时正是这么卡了四百秒。
    /// </summary>
    private static readonly string TestPipe = $"Sentinel.Test.SaveCheck.{Environment.ProcessId}";

    public static int Run(string workDir)
    {
        Directory.CreateDirectory(workDir);

        var configPath = Path.Combine(workDir, "config.json");
        WriteConfig(configPath);

        var serviceExe = FindService();
        if (serviceExe is null)
        {
            Console.WriteLine("找不到 Sentinel.Service.exe —— 先跑一次 dotnet build -c Release。");
            return 1;
        }

        // 管道名必须在建 AgentState 之前设好：ServiceClient 每次发送时现读这个变量。
        Environment.SetEnvironmentVariable("SENTINEL_PIPE", TestPipe);

        using var service = StartService(serviceExe, configPath);

        try
        {
            return CheckWindow(configPath);
        }
        finally
        {
            // `using` 只是释放句柄，**不会杀掉那个进程** —— 不清的话每跑一次就留一个测试服务，
            // 它们还锁着 Release 目录下的 dll，下一次构建直接失败（这一轮就是这么栽的）。
            if (!service.HasExited)
            {
                try
                {
                    service.Kill(entireProcessTree: true);
                    service.WaitForExit(5000);
                }
                catch
                {
                    // 已经退了或者杀不掉：都无所谓，别让它盖住真正的结果。
                }
            }
        }
    }

    private static int CheckWindow(string configPath)
    {

        if (!WaitForService())
        {
            Console.WriteLine("测试服务没有起来（等不到管道）。");
            return 1;
        }

        using var state = new AgentState(new[] { "--config", configPath });

        // 自检：确认手上这份确实是本检查自己写的。光设管道名只是"希望"没人接。
        if (state.Config.Models.Count != 2 || state.Config.Models[1].Id != "half-baked")
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

        window.Show();
        Pump();

        // 检查脚本里绝不能出现真的模态框 —— 弹出来就没人点，整个检查挂死在那儿，
        // 而且看上去像"卡住了"，看不出是哪儿的事。**被拒的理由也藏在这个框里**，
        // 所以这里把它接下来打印出来，而不是弹出去。
        window.AskLeaveDirtyForm = _ => MessageBoxResult.Yes;
        window.RefuseToUser = message =>
        {
            Console.WriteLine();
            Console.WriteLine("被拒了，界面会说的话：");
            Console.WriteLine(message);
            Console.WriteLine();
        };

        Console.WriteLine("配置里有一个没填完的服务商：「半成品」（地址和模型名都是空的）");
        Console.WriteLine();

        var ok = true;

        // 先**不走界面**保存一次，看服务到底答不答应。
        //
        // 为什么先来这一步：被拒时界面会弹一个模态框，脚本点了就永远挂在那儿（什么也看不到）。
        // 直接调用既能拿到"答应/拒绝"，也能拿到拒绝的原话。
        var probe = state.SaveConfig();
        Console.WriteLine($"不走界面直接保存一次: {(probe ? "成功" : "被拒")}");
        Console.WriteLine($"  服务说的 : {state.SaveError ?? "(没有)"}");

        ok &= probe;

        if (!probe)
        {
            // 再点下去只会弹出模态框把检查挂死，没有任何新信息。到此为止。
            window.Close();
            Console.WriteLine();
            Console.WriteLine("结论：保存仍然被拒 —— 修的东西没生效，见上面服务给的原话。");
            return 1;
        }

        // ---- 一、点「新建」加一条规则 —— 用户按不动的那一下
        Console.WriteLine();
        Console.WriteLine("切到规则页…");
        window.SelectTab(3);
        Pump();
        Console.WriteLine($"  切好了（当前页签 {((TabControl)window.FindName("Tabs")!).SelectedIndex}）");

        var before = RuleCount(state);
        Console.WriteLine($"  点「新建」之前，规则数 {before}");

        // 把「新建」会造出来的那条规则照原样拼一份，先在本地校验一遍。
        // 服务用的是同一个校验，所以这里能看到的，正是它等下要说的话 —— 而点下去之后
        // 那句话藏在一个模态框里，脚本看不到（还会把检查挂死）。
        var candidate = ConfigStore.Clone(state.Config);
        candidate.Targets[0].Rules.Add(new Rule
        {
            Id = "probe",
            Name = "新规则",
            PolicyId = candidate.Policies.FirstOrDefault()?.Id ?? "",
            Match = new MatchSpec { Fields = { MatchField.Url } },
        });

        Console.WriteLine("  「新建」会交上去的那份，校验结果：");
        foreach (var issue in ConfigValidator.Validate(candidate))
            Console.WriteLine($"    {issue}");
        Console.WriteLine();

        Click(window, "新建");
        Console.WriteLine("  点过了");
        Pump();
        Console.WriteLine("  界面刷新完了");

        var after = RuleCount(state);
        var status = ((TextBlock)window.FindName("SaveStatusText")!).Text;

        Console.WriteLine($"点「新建」之后");
        Console.WriteLine($"  规则数   : {before} → {after}");
        Console.WriteLine($"  状态栏   : {status}");

        ok &= after == before + 1;
        ok &= status.Contains("已保存");

        // ---- 二、改点别的也能存：换一条规则的启用开关
        window.SelectTab(2);
        Pump();

        // 先在列表里选中那个目标 —— 不选中的话表单是空的，一点「应用修改」就会把进程名清空，
        // 那会**新引入**一个错误（"这个目标会匹配所有窗口"），被拒是理所应当的，
        // 但那不是这条检查要验的东西。
        if (window.FindName("TargetList") is ListBox targetList && targetList.Items.Count > 0)
        {
            targetList.SelectedIndex = 0;
            Pump();
        }

        var enabledBefore = state.Config.Targets[0].Enabled;
        if (window.FindName("TargetEnabledCheck") is CheckBox toggle)
        {
            toggle.IsChecked = !enabledBefore;
            Pump();
            Click(window, "应用修改");
            Pump();
        }

        var enabledAfter = state.Config.Targets[0].Enabled;
        Console.WriteLine();
        Console.WriteLine($"改「启用这个目标」并应用: {enabledBefore} → {enabledAfter}");
        ok &= enabledAfter != enabledBefore;

        // 那处**本来就存在**的错误还在，而且现在只是提醒里的一条 —— 用户可以自己决定什么时候修。
        var issues = ConfigValidator.Validate(state.Config);
        var errors = issues.Where(i => i.Severity == IssueSeverity.Error).ToList();

        Console.WriteLine();
        Console.WriteLine($"半成品还在吗: {state.Config.Models.Count == 2}");
        Console.WriteLine($"本来那处错误还在吗: {errors.Count == 1}");
        Console.WriteLine($"  剩下的错误: {(errors.Count == 0 ? "(没有)" : string.Join(" / ", errors))}");

        ok &= state.Config.Models.Count == 2;
        ok &= errors.Count == 1;                       // 只有"规则指向不存在的策略"那一条
        ok &= issues.Count(i => i.Severity == IssueSeverity.Warning) >= 2;   // 半成品那条 + 规则缺内容那些

        window.Close();

        Console.WriteLine();
        Console.WriteLine(ok
            ? "结论：配置里有没填完的服务商、还有一处本来就坏的地方，都不再挡住新的编辑。"
            : "结论：还有一处不通，见上面。");

        return ok ? 0 : 1;
    }

    private static int RuleCount(AgentState state) => state.Config.Targets.Sum(t => t.Rules.Count);

    /// <summary>
    /// 在当前选中的那个页签里按文字找按钮。
    ///
    /// <para>
    /// 只在当前页签里找，是因为「新建」在目标页、规则页、策略页、时间表页**都有** ——
    /// 全窗口搜的话会点到别的页签那个，检查看起来"卡住"而其实点错了地方。
    /// </para>
    /// </summary>
    private static void Click(Window window, string text)
    {
        if (window.FindName("Tabs") is not TabControl tabs)
            throw new InvalidOperationException("找不到 TabControl。");

        var button = Find(tabs.SelectedContent as DependencyObject, text)
            ?? throw new InvalidOperationException(
                $"在页签 {tabs.SelectedIndex} 上找不到写着「{text}」的按钮。");

        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
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

    private static void WriteConfig(string path)
    {
        var config = new AppConfig();
        config.Ui.DryRun = true;
        config.Logging.LogDirectory = Path.Combine(Path.GetDirectoryName(path)!, "logs");

        // 一个填好的服务商。
        config.Models.Add(new ModelProvider
        {
            Id = "good",
            Name = "DeepSeek",
            Format = ApiFormat.Anthropic,
            Endpoint = "https://api.deepseek.com/anthropic/v1/messages",
            Model = "deepseek-flash",
            ApiKey = "sk-example",
        });

        // 一个没填完的 —— 就是用户被卡住的那个东西。
        config.Models.Add(new ModelProvider { Id = "half-baked", Name = "半成品" });

        config.ActiveModelId = "good";

        config.Policies.Add(new Policy { Id = "p1", Name = "拦下来", Actions = { ActionType.Notify } });

        // 一个目标，好在规则页上按「新建」。里面那条规则**故意指向一个不存在的策略** ——
        // 那是"配置里本来就有一处错误"的情形，用来验证它不会再挡住别的编辑。
        config.Targets.Add(new Target
        {
            Id = "t1",
            DisplayName = "浏览器",
            ProcessNames = { "msedge" },
            Reader = new ReaderOptions { IntervalMs = 1000 },
            Rules =
            {
                new Rule { Id = "r1", Name = "本来就坏的那条", PolicyId = "missing" },
            },
        });

        ConfigStore.Save(config, path);
    }

    private static string? FindService()
    {
        var dir = AppContext.BaseDirectory;

        for (var i = 0; i < 8 && dir is not null; i++, dir = Path.GetDirectoryName(dir))
        {
            var candidate = Path.Combine(dir, "src", "Sentinel.Service", "bin", "Release",
                "net8.0-windows", "Sentinel.Service.exe");

            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static Process StartService(string exe, string configPath)
    {
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        start.ArgumentList.Add("--config");
        start.ArgumentList.Add(configPath);
        start.Environment["SENTINEL_PIPE"] = TestPipe;

        var process = Process.Start(start)!;

        // 服务会一直往 stdout 写；不读走的话管道满了它就会卡住。
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        return process;
    }

    private static bool WaitForService()
    {
        var client = new ServiceClient();

        for (var i = 0; i < 80; i++)
        {
            if (client.TryGetConfig(out _) is not null)
                return true;

            Thread.Sleep(250);
        }

        return false;
    }

    private static void Pump()
    {
        for (var i = 0; i < 4; i++)
            Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
    }
}
