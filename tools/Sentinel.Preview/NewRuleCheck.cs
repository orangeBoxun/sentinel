using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Sentinel.Agent;
using Sentinel.Core.Config;
using Sentinel.Core.Models;
using Sentinel.Core.Unlock;

namespace Sentinel.Preview;

/// <summary>
/// 「点新建、填内容、点应用」这条路上到底发生了什么。
///
/// <para>
/// 起因是用户反复说的一句：<i>我是新建啊 —— 填了内容，点应用，就是存不进去。</i>
/// 而配置里躺着两条**空的**新规则：说明「新建」本身存下去了，填的内容没有。
/// 这条检查就是照着他的动作走一遍：新建 → 往「匹配内容」里打 http → 点应用 → 去文件里看。
/// </para>
/// <para>
/// 顺带盯住一个更坏的可能：<c>ApplyRuleForm</c> 在没有选中规则时是**静默**返回的 ——
/// 点了应用，没保存、没提示、屏幕上还留着你打的字。那比报错难查得多。
/// </para>
/// </summary>
internal static class NewRuleCheck
{
    private static readonly string TestPipe = $"Sentinel.Test.NewRuleCheck.{Environment.ProcessId}";

    public static int Run(string workDir)
    {
        workDir = Path.GetFullPath(workDir);
        Directory.CreateDirectory(workDir);

        Environment.SetEnvironmentVariable("SENTINEL_PIPE", TestPipe);

        var configPath = Path.Combine(workDir, "config.json");
        WriteConfig(configPath);

        // 保护开着（和用户那台一样）——这样"存不下去"就有了第二种可能：被服务拒了。
        // 收紧了就该放行，所以这条路上不该出现拒绝。
        using (var rsa = RSA.Create(2048))
            File.WriteAllText(UnlockAuthority.PublicKeyPathFor(configPath), rsa.ExportSubjectPublicKeyInfoPem());

        var serviceExe = FindService();

        if (serviceExe is null)
        {
            Console.WriteLine("找不到 Sentinel.Service.exe —— 先跑一次 dotnet build -c Release。");
            return 1;
        }

        using var service = StartService(serviceExe, configPath);

        try
        {
            if (!WaitForService())
            {
                Console.WriteLine("测试服务没有起来（等不到管道）。");
                return 1;
            }

            using var state = new AgentState(new[] { "--config", configPath });

            if (state.Config.Targets.Count != 1 || state.Config.Targets[0].Rules[0].Id != "r-doubao-url")
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
                Height = 900,
            };

            var refusals = new List<string>();
            window.RefuseToUser = refusals.Add;
            window.AskLeaveDirtyForm = _ => MessageBoxResult.Yes;   // 别让模态框挂住检查

            window.Show();
            Pump();

            window.SelectTab(3);   // 规则
            Pump();

            var target = state.Config.Targets[0];
            var picker = (ComboBox)window.FindName("RuleTargetPicker")!;
            picker.SelectedItem = target;
            Pump();

            var list = (ListBox)window.FindName("RuleList")!;
            list.SelectedIndex = 0;
            Pump();

            var newButton = (Button)window.FindName("NewRuleButton")!;
            var apply = (Button)window.FindName("ApplyRuleButton")!;
            var patterns = (TextBox)window.FindName("RulePatternsBox")!;
            var name = (TextBox)window.FindName("RuleNameBox")!;
            var form = (FrameworkElement)window.FindName("RuleForm")!;

            var ok = true;

            Console.WriteLine("── 点新建 ──");
            newButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();

            var onDisk = OnDisk(configPath).Targets[0].Rules;
            Console.WriteLine($"文件里现在有 {onDisk.Count} 条：{string.Join(" / ", onDisk.Select(r => $"{r.Id}[{string.Join(",", r.Match.Patterns)}]"))}");
            Console.WriteLine($"界面上：表单可用={form.IsEnabled}  名字框=「{name.Text}」  内容框=「{patterns.Text}」  列表选中={list.SelectedIndex}");
            Console.WriteLine($"这期间被拒过吗={refusals.Count > 0}");

            Report(ref ok, "新建这条存下去了", onDisk.Count == 2, $"文件里 {onDisk.Count} 条规则");
            Report(ref ok, "新建之后表单是活的、选中的就是它", form.IsEnabled && name.Text == "新规则",
                $"表单可用={form.IsEnabled} 名字=「{name.Text}」");

            Console.WriteLine();
            Console.WriteLine("── 往「匹配内容」里打 http，点应用 ──");
            patterns.Text = "http";
            Pump();

            apply.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();

            var after = OnDisk(configPath).Targets[0].Rules;
            var fresh = after.FirstOrDefault(r => r.Id != "r-doubao-url");

            Console.WriteLine($"文件里现在有 {after.Count} 条：{string.Join(" / ", after.Select(r => $"{r.Id}[{string.Join(",", r.Match.Patterns)}]"))}");
            Console.WriteLine($"界面上的内容框=「{patterns.Text}」");
            Console.WriteLine($"这期间被拒过吗={(refusals.Count > 0 ? string.Join(" | ", refusals) : "没有")}");

            Report(ref ok, "填的内容写进文件了",
                fresh is not null && fresh.Match.Patterns.Contains("http"),
                $"新规则的内容={string.Join("/", fresh?.Match.Patterns ?? new List<string>())}");

            Report(ref ok, "点应用之后没有被服务拒", refusals.Count == 0, $"被拒 {refusals.Count} 次");

            // 再来一次：新建第二条、填内容、应用。用户就是这么连点两下的。
            Console.WriteLine();
            Console.WriteLine("── 再新建一条，填内容，点应用 ──");
            newButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();
            patterns.Text = "http" + Environment.NewLine + "douyin";
            Pump();
            apply.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();

            var third = OnDisk(configPath).Targets[0].Rules;
            var newest = third.FirstOrDefault(r => r.Id == "rule-2");
            Console.WriteLine($"文件里现在有 {third.Count} 条：{string.Join(" / ", third.Select(r => $"{r.Id}[{string.Join(",", r.Match.Patterns)}]"))}");
            Console.WriteLine($"被拒过吗={(refusals.Count > 0 ? string.Join(" | ", refusals) : "没有")}");

            Report(ref ok, "第二条也存对了",
                newest is not null && newest.Match.Patterns.Count == 2,
                $"rule-2 的内容={string.Join("/", newest?.Match.Patterns ?? new List<string>())}");

            window.Close();

            Console.WriteLine();
            Console.WriteLine(ok
                ? "结论：新建、填内容、应用 —— 一路上都存得下去，也没有静默失败。"
                : "结论：这条路上有东西没存进去，见上面。");

            return ok ? 0 : 1;
        }
        finally
        {
            Kill(service);
        }
    }

    private static void WriteConfig(string path)
    {
        var config = new AppConfig();
        config.Ui.DryRun = true;
        config.Logging.LogDirectory = Path.Combine(Path.GetDirectoryName(path)!, "logs");

        config.Schedules.Add(new Schedule { Id = "schedule" });

        config.Policies.Add(new Policy
        {
            Id = "p-minimize",
            Name = "提醒并最小化",
            Actions = { ActionType.Notify, ActionType.Minimize },
            GraceSeconds = 3,
            CooldownSeconds = 30,
            DebounceCount = 1,
        });

        // 形状照抄用户那台：一条有内容的规则 + 优先级 10 + 一个计划。
        config.Targets.Add(new Target
        {
            Id = "doubao",
            DisplayName = "豆包工作（内置浏览器）",
            ProcessNames = { "DoubaoWork" },
            Reader = new ReaderOptions { IntervalMs = 2000 },
            Rules =
            {
                new Rule
                {
                    Id = "r-doubao-url",
                    Name = "URL 含 deepseek",
                    Enabled = true,
                    Priority = 10,
                    PolicyId = "p-minimize",
                    ScheduleId = "schedule",
                    Match = new MatchSpec { Fields = { MatchField.Url }, Patterns = { "bilibili", "douyin" } },
                },
            },
        });

        ConfigStore.Save(config, path);
    }

    private static AppConfig OnDisk(string configPath) => ConfigStore.LoadOrDefault(configPath);

    private static void Report(ref bool ok, string what, bool good, string detail)
    {
        Console.WriteLine($"{(good ? "[ok]" : "[!!]")} {what}：{detail}");
        ok &= good;
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

    private static void Kill(Process service)
    {
        try
        {
            if (!service.HasExited)
            {
                service.Kill(entireProcessTree: true);
                service.WaitForExit(5000);
            }
        }
        catch
        {
            // 收尾，出错无所谓。
        }
    }

    private static void Pump()
    {
        for (var i = 0; i < 4; i++)
            Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
    }
}
