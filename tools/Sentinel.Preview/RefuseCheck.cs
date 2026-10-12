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
/// 保存被拒之后，界面必须回到"实际生效的样子"。
///
/// <para>
/// 起因是一个真实的困惑：用户改了规则、点了应用、看到"✓ 配置已保存"（其实是更早那次），
/// 而**表单里还留着他打的那几行** —— 于是以为配好了，实际上服务器那边把那次改动拒了、一个字都没存。
/// 提示弹过一次，但屏幕上那些字是它反面的证据，而人相信屏幕。
/// </para>
/// <para>
/// 这里用"保护开着"的环境造一次真的拒绝：改优先级 = 放宽限制 = 没有令牌就拒。
/// 然后检查那个框有没有被按配置重新填回去。
/// </para>
/// </summary>
internal static class RefuseCheck
{
    private static readonly string TestPipe = $"Sentinel.Test.RefuseCheck.{Environment.ProcessId}";

    public static int Run(string workDir)
    {
        workDir = Path.GetFullPath(workDir);
        Directory.CreateDirectory(workDir);

        Environment.SetEnvironmentVariable("SENTINEL_PIPE", TestPipe);

        var configPath = Path.Combine(workDir, "config.json");
        WriteConfig(configPath);

        // 放一把公钥进去，服务就会认为解锁保护开着 —— 于是"放宽"会被拒。
        // 私钥不留：这个检查只关心"被拒"这个状态。
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

            if (state.Config.Targets.Count != 1 || state.Config.Targets[0].Rules[0].Id != "r1")
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

            var refused = false;
            window.RefuseToUser = _ => refused = true;   // 别让模态框把检查挂住

            window.Show();
            Pump();

            window.SelectTab(3);   // 规则
            Pump();

            var list = (ListBox)window.FindName("RuleList")!;
            list.SelectedIndex = 0;
            Pump();

            var priority = (TextBox)window.FindName("RulePriorityBox")!;
            var patterns = (TextBox)window.FindName("RulePatternsBox")!;
            var apply = (Button)window.FindName("ApplyRuleButton")!;

            Console.WriteLine($"改之前   : 优先级={priority.Text}  内容={patterns.Text.Replace(Environment.NewLine, "/")}");

            // 改优先级 —— 按设计这算"放宽"，保护开着又没有令牌，所以会被拒。
            priority.Text = "99";
            patterns.Text = "bilibili" + Environment.NewLine + "douyin" + Environment.NewLine + "http";
            Pump();

            apply.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();

            var onDisk = OnDisk(configPath).Targets[0].Rules[0];

            Console.WriteLine($"点过应用 : 被拒了吗={refused}");
            Console.WriteLine($"磁盘上   : 优先级={onDisk.Priority}  内容={string.Join("/", onDisk.Match.Patterns)}");
            Console.WriteLine($"界面上   : 优先级={priority.Text}  内容={patterns.Text.Replace(Environment.NewLine, "/")}");

            var ok = true;

            Report(ref ok, "这次改动确实被拒了", refused && onDisk.Priority == 0,
                $"被拒={refused}，磁盘上的优先级={onDisk.Priority}");

            Report(ref ok, "界面的优先级回到了配置里的值", priority.Text == "0",
                $"框里是「{priority.Text}」");

            Report(ref ok, "界面的内容也回到了配置里的值",
                patterns.Text.Replace(Environment.NewLine, "/") == "old",
                $"框里是「{patterns.Text.Replace(Environment.NewLine, "/")}」");

            window.Close();

            Console.WriteLine();
            Console.WriteLine(ok
                ? "结论：被拒之后界面回到了实际生效的样子 —— 不会再出现「屏幕上配好了、其实没存」。"
                : "结论：界面还留着被拒的内容，见上面。");

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

        config.Policies.Add(new Policy
        {
            Id = "p1",
            Name = "提醒并最小化",
            Actions = { ActionType.Notify, ActionType.Minimize },
            GraceSeconds = 0,
            CooldownSeconds = 0,
            DebounceCount = 1,
        });

        config.Targets.Add(new Target
        {
            Id = "t1",
            DisplayName = "豆包工作",
            ProcessNames = { "DoubaoWork" },
            Reader = new ReaderOptions { IntervalMs = 2000 },
            Rules =
            {
                new Rule
                {
                    Id = "r1",
                    Name = "禁止浏览内置浏览器",
                    Enabled = true,
                    Priority = 0,
                    PolicyId = "p1",
                    Match = new MatchSpec { Fields = { MatchField.Url }, Patterns = { "old" } },
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
