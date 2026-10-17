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
/// 真的去点一次「暂停」，看它到底有没有生效。
///
/// <para>
/// 单测覆盖的是"规则在暂停里就不动手"那一段。但**按钮 → Agent → 命名管道 → 服务 → 写账本**
/// 这一整条链没有测过，而这一条链上任何一环错位，表现都是"点了没反应"—— 没有异常、没有日志、
/// 界面上什么都不会说。这一轮我就是把 IPC 命令名从 <c>pause-target</c> 改成了 <c>pause-rule</c>，
/// 正是这种"改了一边忘了另一边就静默失效"的地方。
/// </para>
/// <para>
/// 跑在隔离环境里：自己的管道名、临时目录里的配置、临时目录里的账本。不碰用户装好的那一套。
/// 配置设成安全模式 + 只记录，万一这个测试服务真的拉起了一个 Agent，它也不会动任何窗口。
/// </para>
/// </summary>
internal static class PauseCheck
{
    private const string TestPipe = "Sentinel.Test.PauseCheck";

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
            if (!WaitForService())
            {
                Console.WriteLine("测试服务没有起来（等不到管道）。");
                return 1;
            }

            Console.WriteLine("测试服务已就绪。");
            Console.WriteLine();

            return DriveWindow(configPath);
        }
        finally
        {
            try
            {
                service.Kill(entireProcessTree: true);
            }
            catch
            {
                // 子进程可能已经自己退了。
            }
        }
    }

    /// <summary>打开真窗口，选中真规则，点真按钮。</summary>
    private static int DriveWindow(string configPath)
    {
        // 必须和服务用同一个配置路径。线上是服务用 `--service --config "<路径>"` 把 Agent 拉起来的，
        // 少了这个参数，Agent 会去读默认位置 —— 那它读的账本和服务写的就不是同一个文件，
        // 于是"暂停明明记上了，界面却还说现在可以暂停"。这个坑正好在第一次跑的时候露出来了。
        using var state = new AgentState(new[] { "--config", configPath });

        // 最后一道保险：确认手上这个配置确实是本检查自己写的那一份。光设隔离管道名只是"希望"
        // 没人接，这一步才是"确认"—— 否则这个检查可能正在拿用户的真配置做实验。
        if (state.Config.Targets.Count != 1 || state.Config.Targets[0].Id != "t1")
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

        window.SelectTab(3);   // 规则
        Pump();

        if (window.FindName("RuleTargetPicker") is ComboBox picker && picker.Items.Count > 0)
        {
            picker.SelectedIndex = 0;
            Pump();
        }

        if (window.FindName("RuleList") is not ListBox list || list.Items.Count == 0)
        {
            Console.WriteLine("规则列表是空的 —— 配置没读到，后面的就都不用看了。");
            return 1;
        }

        list.SelectedIndex = 0;
        Pump();

        var button = (Button)window.FindName("PauseButton")!;
        var text = (TextBlock)window.FindName("PauseText")!;
        var ledger = Path.Combine(Path.GetDirectoryName(configPath)!, ".paused.json");

        Console.WriteLine("== 点之前 ==");
        Console.WriteLine($"  按钮可用   : {button.IsEnabled}");
        Console.WriteLine($"  按钮文字   : {button.Content}");
        Console.WriteLine($"  界面提示   : {text.Text}");
        Console.WriteLine($"  账本存在   : {File.Exists(ledger)}");
        Console.WriteLine();

        if (!button.IsEnabled)
        {
            Console.WriteLine("按钮是灰的 —— 暂停根本没测到。");
            return 1;
        }

        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Pump();

        Console.WriteLine("== 点了一次 ==");
        Console.WriteLine($"  界面提示   : {text.Text}");
        Console.WriteLine($"  账本内容   : {Read(ledger)}");
        Console.WriteLine();

        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Pump();

        Console.WriteLine("== 紧接着又点一次（额度应当已经花掉）==");
        Console.WriteLine($"  界面提示   : {text.Text}");
        Console.WriteLine();

        window.Close();
        return 0;
    }

    private static string Read(string path) =>
        File.Exists(path) ? File.ReadAllText(path).Trim() : "(没有写)";

    // ------------------------------------------------------------------ 隔离环境

    private static void WriteConfig(string path)
    {
        var config = new AppConfig();

        // 安全模式：这个测试服务可能会顺手拉起一个 Agent，让它只会记录、不会动任何窗口。
        config.Ui.DryRun = true;
        config.Logging.LogDirectory = Path.Combine(Path.GetDirectoryName(path)!, "logs");

        config.Policies.Add(new Policy
        {
            Id = "p1",
            Name = "只记录",
            Actions = { ActionType.Log },
            DebounceCount = 1,
        });

        config.Targets.Add(new Target
        {
            Id = "t1",
            DisplayName = "暂停测试目标",
            ProcessNames = { "notepad" },
            Reader = new ReaderOptions { IntervalMs = 0 },
            Rules =
            {
                new Rule
                {
                    Id = "r1",
                    Name = "暂停测试规则",
                    PolicyId = "p1",
                    // 额度开好，正是"保护锁上之前应该做的那一步"。
                    Pause = new PauseOptions { Enabled = true, Workday = new PauseBudget { Times = 2, Minutes = 10 }, Weekend = new PauseBudget { Times = 2, Minutes = 10 } },
                    Match = new MatchSpec
                    {
                        Type = MatchKind.Keyword,
                        Fields = { MatchField.Url },
                        Patterns = { "example.com" },
                    },
                },
            },
        });

        ConfigStore.Save(config, path);
    }

    private static string? FindService()
    {
        var dir = AppContext.BaseDirectory;

        for (var i = 0; i < 8 && dir is not null; i++, dir = Path.GetDirectoryName(dir))
        {
            var candidate = Path.Combine(dir, "src", "Sentinel.Service", "bin", "Release", "net8.0-windows", "Sentinel.Service.exe");
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
