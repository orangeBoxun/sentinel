using System.Diagnostics;
using System.IO;
using Sentinel.Agent;
using Sentinel.Core.Abstractions;
using Sentinel.Core.Config;
using Sentinel.Core.Logging;
using Sentinel.Core.Models;

namespace Sentinel.Preview;

/// <summary>
/// 审计现在有两份：Agent 本地一份，服务一份（权威的）。
///
/// <para>
/// 这条链上"做错了"是**看不出来**的：本地那份照写，界面照读 —— 而服务那份可能压根没写成功。
/// 于是"记录改得动"这件事看起来跟修好了一样。所以这里真的起一个服务，走一遍
/// Agent → 管道 → 服务 → 磁盘，然后去那个目录里数行数。
/// </para>
/// <para>
/// 隔离环境：自己的管道名、临时目录里的配置。不碰用户装好的那一套。
/// </para>
/// </summary>
internal static class AuditCheck
{
    private static readonly string TestPipe = $"Sentinel.Test.AuditCheck.{Environment.ProcessId}";

    public static int Run(string workDir)
    {
        workDir = Path.GetFullPath(workDir);
        Directory.CreateDirectory(workDir);

        var configPath = Path.Combine(workDir, "config.json");

        var config = new AppConfig();
        ConfigStore.Save(config, configPath);

        var serviceExe = FindService();
        if (serviceExe is null)
        {
            Console.WriteLine("找不到 Sentinel.Service.exe —— 先跑一次 dotnet build -c Release。");
            return 1;
        }

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

            var logsDir = Path.Combine(workDir, "logs");
            var auditDir = Path.Combine(workDir, "audit");
            var client = new ServiceClient();

            using (var audit = new ServiceAuditLog(new DailyAuditLog(logsDir), client))
            {
                for (var i = 1; i <= 3; i++)
                {
                    audit.Write(new AuditEntry(
                        DateTimeOffset.Now.AddSeconds(-i), "t1", "r1", "close", null, $"第 {i} 条测试记录"));
                }

                // 后台线程是两秒一轮，给它跑一轮的时间。
                Thread.Sleep(TimeSpan.FromSeconds(4));
            }

            var authoritative = LatestFile(auditDir);
            var local = LatestFile(logsDir);

            Console.WriteLine($"权威那份（服务写的）: {(authoritative is null ? "(没有)" : authoritative)}");
            Console.WriteLine($"本地那份（Agent 写的）: {(local is null ? "(没有)" : local)}");
            Console.WriteLine();

            var ok = true;

            var visited = authoritative is null ? 0 : AuditFile.Tail(authoritative, 100).Count;
            var localLines = local is null ? 0 : AuditFile.Tail(local, 100).Count;

            Report(ref ok, "服务那份写下来了，三条都在", visited == 3, $"{visited} 行");
            Report(ref ok, "本地那份也在（服务不在时不能丢）", localLines == 3, $"{localLines} 行");

            if (authoritative is not null)
            {
                var body = string.Join(Environment.NewLine, AuditFile.Tail(authoritative, 100));
                Report(ref ok, "内容是原样的", body.Contains("第 3 条测试记录"), body.Split('\n')[0][..Math.Min(90, body.Length)]);
            }

            Console.WriteLine();
            Console.WriteLine(ok
                ? "结论：审计两份都到位了 —— 权威的那份经过管道落到了服务的目录里。"
                : "结论：有一样不对，见上面。");

            return ok ? 0 : 1;
        }
        finally
        {
            if (!service.HasExited)
            {
                try
                {
                    service.Kill(entireProcessTree: true);
                    service.WaitForExit(5000);
                }
                catch
                {
                    // 已经退了或者杀不掉：都无所谓。
                }
            }
        }
    }

    private static string? LatestFile(string dir) =>
        Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.jsonl").OrderByDescending(f => f).FirstOrDefault()
            : null;

    private static void Report(ref bool ok, string what, bool good, string detail)
    {
        Console.WriteLine($"{(good ? "✓" : "✗")} {what}：{detail}");
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
}
