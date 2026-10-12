using System.Diagnostics;
using System.IO;
using Sentinel.Core.Config;
using Sentinel.Core.Models;
using Sentinel.Core.Vault;

namespace Sentinel.Preview;

/// <summary>
/// 「看门狗被停掉 → 保险箱封印」这条规则，两个方向都要验。
///
/// <para>
/// <b>误判的代价很贵</b>：封印意味着保险箱只能用监护人解锁码打开，而那串码在另一台设备上。
/// 所以比起"该封的时候封了"，更该证明的是"不该封的时候没封" —— 比如一台从来没装过看门狗的
/// 机器，或者装的时候用了 <c>-NoWatchdog</c>，都绝不能在正常安装之后被封印。
/// </para>
/// <para>
/// 隔离环境：自己的管道名、临时目录里的配置。不碰用户装好的那一套。
/// </para>
/// </summary>
internal static class WatchdogCheck
{
    public static int Run(string workDir)
    {
        workDir = Path.GetFullPath(workDir);
        Directory.CreateDirectory(workDir);

        var serviceExe = FindService();
        if (serviceExe is null)
        {
            Console.WriteLine("找不到 Sentinel.Service.exe —— 先跑一次 dotnet build -c Release。");
            return 1;
        }

        var ok = true;

        // ---- 一、看门狗的心跳停了 → 该封印
        var frozen = Path.Combine(workDir, "停了");
        Prepare(frozen, createVault: true);

        // 心跳停在一小时前：看门狗不再运行。
        //
        // 判据是文件的**修改时间**（服务读的是 File.GetLastWriteTimeUtc），所以光把旧时刻写进
        // 内容里没用 —— 那只能骗人，骗不过判断。得把时间戳本身改到过去。
        var alive = VaultGuard.WatchdogAlivePathFor(Path.Combine(frozen, "config.json"));
        File.WriteAllText(alive, DateTimeOffset.UtcNow.AddHours(-1).ToString("O"));
        File.SetLastWriteTimeUtc(alive, DateTime.UtcNow.AddHours(-1));

        var sealedNow = RunServiceAndWait(serviceExe, frozen, () =>
            File.Exists(VaultGuard.SealPathFor(Path.Combine(frozen, "config.json"))), TimeSpan.FromSeconds(90));

        var reason = sealedNow
            ? VaultGuard.ReadSeal(Path.Combine(frozen, "config.json")) ?? "(读不出来)"
            : "(没有封印)";

        Report(ref ok, "看门狗停了 → 保险箱被封印", sealedNow, Compact(reason));

        // ---- 二、从来没跑过看门狗（老安装、或者 -NoWatchdog）→ **不许**封印
        var fresh = Path.Combine(workDir, "新装");
        Prepare(fresh, createVault: true);

        var sealedWrongly = RunServiceAndWait(serviceExe, fresh, () =>
            File.Exists(VaultGuard.SealPathFor(Path.Combine(fresh, "config.json"))), TimeSpan.FromSeconds(45));

        Report(ref ok, "从来没有过心跳 → 不封印（这是误判防线）", !sealedWrongly,
            sealedWrongly ? "**被误判了** —— 正常安装之后保险箱就被封了" : "没有封印，对");

        Console.WriteLine();
        Console.WriteLine(ok
            ? "结论：停掉看门狗会被封印，而正常安装不会 —— 两个方向都对。"
            : "结论：有一样不对，见上面。");

        return ok ? 0 : 1;
    }

    /// <summary>造一个隔离的安装环境：配置 + 一个（假的）保险箱。</summary>
    private static void Prepare(string dir, bool createVault)
    {
        Directory.CreateDirectory(dir);

        var configPath = Path.Combine(dir, "config.json");

        var config = new AppConfig();
        ConfigStore.Save(config, configPath);

        if (!createVault)
            return;

        // 封印只对"真的有保险箱"的机器有意义（VaultGuard.VaultExists 就是在看这个文件）。
        // 内容不重要：封印标记才是判定依据，内容只是顺带要改的那层包装。
        var vault = VaultGuard.VaultRootFor(configPath);
        Directory.CreateDirectory(vault);
        File.WriteAllText(VaultGuard.KeysPathFor(configPath), """{"Guardian":null,"Password":null}""");
    }

    /// <summary>起一个隔离的服务，等 <paramref name="until"/> 成立（或者等超时），然后把它停掉。</summary>
    private static bool RunServiceAndWait(string exe, string dir, Func<bool> until, TimeSpan timeout)
    {
        var pipe = $"Sentinel.Test.WatchdogCheck.{Environment.ProcessId}.{Path.GetFileName(dir)}";
        var output = new List<string>();

        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        start.ArgumentList.Add("--config");
        start.ArgumentList.Add(Path.Combine(dir, "config.json"));
        start.Environment["SENTINEL_PIPE"] = pipe;

        using var service = Process.Start(start)!;

        // 服务的输出收起来：这条检查失败时，答案几乎总在里面（它自己会说"跳过对看门狗的看管"
        // 之类的）。丢掉输出等于把唯一在现场的证人请出去。
        service.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.Add(e.Data); };
        service.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.Add("ERR " + e.Data); };
        service.BeginOutputReadLine();
        service.BeginErrorReadLine();

        var reached = false;

        try
        {
            var deadline = DateTime.UtcNow + timeout;

            while (DateTime.UtcNow < deadline)
            {
                if (until())
                {
                    reached = true;
                    break;
                }

                if (service.HasExited)
                    break;

                Thread.Sleep(500);
            }

            if (!reached)
                reached = until();

            return reached;
        }
        finally
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

            if (!reached)
            {
                Console.WriteLine($"  [服务 {Path.GetFileName(dir)} 的输出，退出={service.HasExited}]");

                lock (output)
                {
                    foreach (var line in output.TakeLast(15))
                        Console.WriteLine("    " + line);
                }
            }
        }
    }

    private static string Compact(string text) =>
        text.Replace("\n", " ").Replace("\r", " ") is var flat && flat.Length > 110
            ? flat[..110] + "…"
            : flat;

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
}
