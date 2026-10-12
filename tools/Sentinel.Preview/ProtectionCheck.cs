using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Sentinel.Agent;
using Sentinel.Core.Config;
using Sentinel.Core.Models;
using Sentinel.Core.Unlock;

namespace Sentinel.Preview;

/// <summary>
/// 解锁保护开着的时候，「停止监控」到底拦没拦住。
///
/// <para>
/// 起因：它只取消本地的监控循环 —— **不写配置、不经过服务、不碰令牌判定**，是所有"放宽"
/// 里唯一一条完全在受管那套之外的路。而且停掉之后服务也救不回来（心跳在循环里发，循环一停
/// 心跳就停；服务拉的新 Agent 撞上单实例锁秒退，因为旧进程还活着占着它）。
/// </para>
/// <para>
/// 做法：临时配置 + 一个隔离的测试服务 + **往配置目录里放一把公钥**，服务就会认为保护开着。
/// 不碰用户装好的那一套。
/// </para>
/// </summary>
internal static class ProtectionCheck
{
    private const string TestPipe = "Sentinel.Test.ProtectionCheck";

    public static int Run(string workDir)
    {
        Environment.SetEnvironmentVariable("SENTINEL_PIPE", TestPipe);
        Directory.CreateDirectory(workDir);

        var configPath = Path.Combine(workDir, "config.json");
        WriteConfig(configPath);

        // 放一把公钥进去，服务就会认为解锁保护开着。私钥不留 —— 这个检查只关心"保护开着"这个状态。
        using (var rsa = RSA.Create(2048))
            File.WriteAllText(UnlockAuthority.PublicKeyPathFor(configPath), rsa.ExportSubjectPublicKeyInfoPem());

        var serviceExe = FindService();
        if (serviceExe is null)
        {
            Console.WriteLine("找不到 Sentinel.Service.exe —— 先 dotnet build -c Release。");
            return 1;
        }

        using var service = StartService(serviceExe, configPath);

        try
        {
            if (!WaitForService())
            {
                Console.WriteLine("测试服务没有起来。");
                return 1;
            }

            using var state = new AgentState(new[] { "--config", configPath });

            // 先确认这个检查真的造出了"保护开着"的状态，否则后面看到的都是假的。
            if (!state.UnlockConfigured)
            {
                Console.WriteLine("服务没认出那把公钥 —— 这个检查的前提没成立，不往下走了。");
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

            var stop = (Button)window.FindName("StopMonitorButton")!;
            var release = (Button)window.FindName("ReleaseButton")!;

            Console.WriteLine($"解锁保护已开启   : {state.UnlockConfigured}");
            Console.WriteLine($"「停止监控」可用 : {stop.IsEnabled}");
            Console.WriteLine($"「关闭保护」可见 : {release.Visibility}");
            Console.WriteLine($"灰掉的提示       : {stop.ToolTip}");
            Console.WriteLine();

            // 说明：这里**不去点那个按钮**。它的处理函数里有一个 MessageBox，没人点确认就会
            // 一直卡住 —— 在一个无人值守的检查里触发它，只会把检查本身挂死。
            // 而界面上的按钮一旦 IsEnabled=false，用户根本点不动它，所以这一条足够说明问题。
            var ok = !stop.IsEnabled && release.Visibility == Visibility.Visible;

            Console.WriteLine(ok
                ? "结论：保护开着的时候停不了监控。"
                : "结论：**没拦住** —— 按钮还是可用的。");

            window.Close();
            return ok ? 0 : 1;
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

    private static void WriteConfig(string path)
    {
        var config = new AppConfig();
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
            DisplayName = "测试目标",
            ProcessNames = { "notepad" },
            Reader = new ReaderOptions { IntervalMs = 0 },
            Rules =
            {
                new Rule
                {
                    Id = "r1",
                    Name = "测试规则",
                    PolicyId = "p1",
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
