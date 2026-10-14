using System.Diagnostics;
using System.IO;
using Sentinel.Core.Config;
using Sentinel.Core.Models;
using Sentinel.Core.Vault;

namespace Sentinel.Preview;

/// <summary>
/// 「什么时候才该因为看门狗而封印保险箱」这条规则，要验的是**不该封的时候没封**。
///
/// <para>
/// <b>误判的代价是不可逆的</b>：封印之后保险箱只能用监护人解锁码打开，而那串码在另一台设备上。
/// 2026-10-12 就真封错过一次：用户合上笔记本待机 8 分钟，看门狗任务和服务都停了，醒来时
/// 心跳文件"过期" 8 分钟（阈值是 5 分钟）→ 保险箱被封印。所以心跳变旧**不能**当判据，
/// 判据是"计划任务真的不见了"。
/// </para>
/// <para>
/// <b>"任务不见了"那一个方向只能靠人去看，不能在这里造。</b> 计划任务是全机器一个名字，
/// 检查去删它就是在动用户真装的那一套 —— 而删掉之后服务会补一个封印，封的是**真实的保险箱**。
/// 这正是这条检查踩过的坑，所以这里只验"心跳停/从来没有心跳 → 不许封印"。
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

        // ---- 一、心跳停了一小时，但任务还在 → **不许**封印
        var frozen = Path.Combine(workDir, "心跳停了");
        Prepare(frozen, createVault: true);

        // 心跳停在一小时前。
        //
        // 判据是文件的**修改时间**（服务读的是 File.GetLastWriteTimeUtc），所以光把旧时刻写进
        // 内容里没用 —— 那只能骗人，骗不过判断。得把时间戳本身改到过去。
        var alive = VaultGuard.WatchdogAlivePathFor(Path.Combine(frozen, "config.json"));
        File.WriteAllText(alive, DateTimeOffset.UtcNow.AddHours(-1).ToString("O"));
        File.SetLastWriteTimeUtc(alive, DateTime.UtcNow.AddHours(-1));

        var sealedWrongly = RunServiceAndWait(serviceExe, frozen, () =>
            File.Exists(VaultGuard.SealPathFor(Path.Combine(frozen, "config.json"))), TimeSpan.FromSeconds(45), out var frozenLog);

        Report(ref ok, "心跳停了一小时（机器待机过）→ 不封印", !sealedWrongly,
            sealedWrongly
                ? $"**被误判了**：{Compact(VaultGuard.ReadSeal(Path.Combine(frozen, "config.json")) ?? "")}"
                : "没有封印，对 —— 心跳旧不是拆解的证明");

        // 而且要能看出它走的是哪条路：心跳旧 → 只出声提醒（"空转"），不是封印。
        var warned = frozenLog.Any(l => l.Contains("空转"));
        Report(ref ok, "心跳旧时它只是出个声（而不是动封印）", warned,
            warned ? "日志里说了'它在空转'" : "日志里没看到那句提醒");

        // ---- 二、从来没跑过看门狗（老安装、或者 -NoWatchdog）→ **不许**封印
        var fresh = Path.Combine(workDir, "新装");
        Prepare(fresh, createVault: true);

        var sealedFresh = RunServiceAndWait(serviceExe, fresh, () =>
            File.Exists(VaultGuard.SealPathFor(Path.Combine(fresh, "config.json"))), TimeSpan.FromSeconds(45), out _);

        Report(ref ok, "从来没有过心跳 → 不封印（这是误判防线）", !sealedFresh,
            sealedFresh ? "**被误判了** —— 正常安装之后保险箱就被封了" : "没有封印，对");

        Console.WriteLine();
        Console.WriteLine(ok
            ? "结论：心跳变旧（待机、机器忙）不再封印保险箱 —— 误封那条路堵上了。"
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
    private static bool RunServiceAndWait(
        string exe, string dir, Func<bool> until, TimeSpan timeout, out List<string> log)
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

            lock (output)
                log = [.. output];

            if (!reached)
            {
                Console.WriteLine($"  [服务 {Path.GetFileName(dir)} 的输出，退出={service.HasExited}]");

                foreach (var line in log.TakeLast(15))
                    Console.WriteLine("    " + line);
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
