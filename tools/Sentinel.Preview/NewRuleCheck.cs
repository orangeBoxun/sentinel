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
            var stagedMark = (TextBlock)window.FindName("RuleStagedMark")!;

            var ok = true;

            Console.WriteLine("── 点新建 ──");
            newButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();

            var onDisk = OnDisk(configPath).Targets[0].Rules;
            Console.WriteLine($"文件里现在有 {onDisk.Count} 条：{string.Join(" / ", onDisk.Select(r => $"{r.Id}[{string.Join(",", r.Match.Patterns)}]"))}");
            Console.WriteLine($"界面上：表单可用={form.IsEnabled}  名字框=「{name.Text}」  内容框=「{patterns.Text}」  列表选中={list.SelectedIndex}");
            Console.WriteLine($"这期间被拒过吗={refusals.Count > 0}");

            Report(ref ok, "点新建**没有**写进配置（没填完的东西不该落盘）", onDisk.Count == 1,
                $"文件里还是 {onDisk.Count} 条规则，多出来的那条只在界面上");
            Report(ref ok, "但界面上列表里能看到它，表单是活的",
                list.Items.Count == 2, $"界面上 {list.Items.Count} 条");
            Report(ref ok, "选中的就是那条草稿", form.IsEnabled && name.Text == "新规则",
                $"表单可用={form.IsEnabled} 名字=「{name.Text}」");

            // 草稿在列表里和存好的规则长得一样 —— 不说一句，人会以为已经保存了。
            Report(ref ok, "界面上明说这条还没进配置",
                stagedMark.Visibility == Visibility.Visible,
                stagedMark.Visibility == Visibility.Visible ? "说了" : "没说，看起来像已经存了");

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
            Report(ref ok, "存下去之后那行提示消失了",
                stagedMark.Visibility == Visibility.Collapsed,
                stagedMark.Visibility == Visibility.Collapsed ? "消失了" : "还挂着");

            // ---- 用户真正踩的那个：新建之后把匹配方式改成「模型鉴定」再应用 ----
            //
            // 以前点新建会立刻把一条**关键词**规则写进配置，于是之后把它改成模型鉴定，
            // 服务判成"同一条规则换了匹配方式 = 放宽"，要令牌。人只是想把这条规则填完，
            // 什么都没想放宽。草稿不落盘之后，这一次就是"新增一条规则"：收紧，免费。
            Console.WriteLine();
            Console.WriteLine("── 再新建一条，匹配方式改成「模型鉴定」，填一句话，点应用 ──");

            newButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();

            var typeBox = (ComboBox)window.FindName("RuleTypeBox")!;
            typeBox.SelectedItem = ((IEnumerable<Choice<MatchKind>>)typeBox.ItemsSource!)
                .First(c => c.Value == MatchKind.Model);
            Pump();

            patterns.Text = "和学习无关的娱乐内容";
            Pump();

            var refusalsBefore = refusals.Count;

            apply.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();

            var third = OnDisk(configPath).Targets[0].Rules;
            var modelRule = third.FirstOrDefault(r => r.Match.Type == MatchKind.Model);

            Console.WriteLine($"文件里现在有 {third.Count} 条：{string.Join(" / ", third.Select(r => $"{r.Id}({r.Match.Type})[{string.Join(",", r.Match.Patterns)}]"))}");
            Console.WriteLine($"被拒过吗={(refusals.Count > 0 ? string.Join(" | ", refusals) : "没有")}");

            Report(ref ok, "把匹配方式改成「模型鉴定」也存得进去，没有被当成放宽",
                modelRule is not null && refusals.Count == refusalsBefore,
                modelRule is null
                    ? "没进去"
                    : $"进去了：{modelRule.Id}（{modelRule.Match.Type}）内容={string.Join("/", modelRule.Match.Patterns)}");

            // ---- 后台把配置整体换掉之后，界面还存得进去吗 ----
            //
            // 这一步才是用户那台机器上的真实情形。心跳发现"版本变了"（**包括界面自己刚存的那次**
            // 也会让版本变），MonitorHost 就在后台叫一句 ReloadConfig —— 整份配置换成一批新对象，
            // 而界面毫不知情：列表和 _selectedRule 指的还是上一批。
            // 于是表单写进的是个孤儿对象，保存时发出去的却是新配置，改动不在里面。
            Console.WriteLine();
            Console.WriteLine("── 让配置在后台被重载一次（心跳发现版本变了时就是这么做的）──");
            state.ReloadConfig();
            Console.WriteLine($"重载之后 _state.Config 里的目标数={state.Config.Targets.Count}（换了新对象）");
            Pump();

            var beforeCount = OnDisk(configPath).Targets[0].Rules.Count;

            newButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();

            Console.WriteLine($"点过新建：文件里 {OnDisk(configPath).Targets[0].Rules.Count} 条，界面上 {list.Items.Count} 条");

            patterns.Text = "http";
            Pump();
            apply.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();

            var afterReload = OnDisk(configPath).Targets[0].Rules;
            Console.WriteLine($"点过应用：文件里 {afterReload.Count} 条：{string.Join(" / ", afterReload.Select(r => $"{r.Id}[{string.Join(",", r.Match.Patterns)}]"))}");
            Console.WriteLine($"界面上还是 {list.Items.Count} 条，选中的第 {list.SelectedIndex} 条");

            Report(ref ok, "重载之后新建的规则也写进了文件", afterReload.Count > beforeCount,
                $"新建前 {beforeCount} 条，现在 {afterReload.Count} 条");

            Report(ref ok, "重载之后填的内容也写进了文件",
                afterReload.Any(r => r.Match.Patterns.Contains("http") && r.Id.StartsWith("rule")),
                $"带 http 的规则：{string.Join("/", afterReload.Where(r => r.Match.Patterns.Contains("http")).Select(r => r.Id))}");

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
