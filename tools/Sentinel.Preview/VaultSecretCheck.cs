using System.Diagnostics;
using System.IO;
using System.Windows;
using Sentinel.Agent;
using Sentinel.Core.Config;
using Sentinel.Core.Models;
using Sentinel.Core.Vault;

namespace Sentinel.Preview;

/// <summary>
/// 「本机秘密」这条线通不通：服务给得出来、给的是同一份、而且**它真的把密码门绑住了**。
///
/// <para>
/// 这是甲那件事的接线检查。逻辑本身有单元测试，但"服务 → 管道 → 会话"这段接线最容易
/// 悄没声地断掉：断了之后保险箱会以"密码不对"的样子表现出来 —— 那是最难查的一种坏法，
/// 因为看起来像用户记错了密码。
/// </para>
/// <para>
/// 隔离环境：自己的管道名、临时目录里的配置。不碰用户装好的那一套。
/// </para>
/// </summary>
internal static class VaultSecretCheck
{
    private static readonly string TestPipe = $"Sentinel.Test.VaultSecretCheck.{Environment.ProcessId}";

    public static int Run(string workDir)
    {
        workDir = Path.GetFullPath(workDir);
        Directory.CreateDirectory(workDir);

        Environment.SetEnvironmentVariable("SENTINEL_PIPE", TestPipe);

        var configPath = Path.Combine(workDir, "config.json");
        ConfigStore.Save(new AppConfig(), configPath);

        var serviceExe = FindService();

        if (serviceExe is null)
        {
            Console.WriteLine("找不到 Sentinel.Service.exe —— 先跑一次 dotnet build -c Release。");
            return 1;
        }

        using var service = StartService(serviceExe, configPath, out var serviceLog);

        try
        {
            if (!WaitForService())
            {
                Console.WriteLine("测试服务没有起来（等不到管道）。");
                return 1;
            }

            using var state = new AgentState(new[] { "--config", configPath });

            var ok = true;

            var first = state.VaultSecret();
            var second = state.VaultSecret();

            Console.WriteLine($"服务给的秘密：{first?.Length ?? -1} 字节；再问一次：{(second is null ? "没有" : $"{second.Length} 字节")}");

            Report(ref ok, "服务能给出发秘密（32 字节）", first is { Length: 32 },
                first is null ? "拿不到 —— 服务不认识这条命令？" : $"{first.Length} 字节");

            Report(ref ok, "两次问拿到的是同一份", first is not null && second is not null
                    && first.SequenceEqual(second),
                "换一份就意味着保险箱会随每次询问而失效");

            var secretPath = VaultGuard.MachineSecretPathFor(configPath);
            Report(ref ok, "秘密落在 vault 目录之外（备份带不走它）",
                File.Exists(secretPath) && !secretPath.StartsWith(VaultGuard.VaultRootFor(configPath), StringComparison.OrdinalIgnoreCase),
                secretPath);

            // 端到端：用服务给的秘密建一个保险箱，再用密码打开它。
            using var session = new VaultSession(configPath, null, state.VaultSecret);

            var code = VaultCrypto.NewGuardianCode();
            var created = session.Create("这是一个足够长的密码", code);

            Report(ref ok, "用这份秘密能建保险箱", created.Ok, created.Error ?? "建好了");

            session.Lock();

            var opened = session.UnlockWithPassword("这是一个足够长的密码");
            Report(ref ok, "建完能用密码打开（说明绑定是通的）", opened.Ok, opened.Error ?? "打开了");

            // ---- 抹封印标记这条命令：只有监护码对才认
            //
            // 标记在配置目录里，Agent（普通用户）删不掉 —— 所以它得请服务来删。而"请服务删"
            // 必须先验监护码，否则那就是一条"谁喊一声都能解开封印"的后门。
            var sealPath = VaultGuard.SealPathFor(configPath);
            File.WriteAllText(sealPath, """{"Reason":"检查写的","At":"2026-01-01 00:00:00"}""");

            var wrong = state.ClearSeal(VaultCrypto.NewGuardianCode());
            Report(ref ok, "监护码不对时拒绝解除封印（这条是后门防线）",
                wrong is not null && File.Exists(sealPath),
                wrong is null ? "居然认了！" : $"被拒了：{wrong}");

            var right = state.ClearSeal(code);
            Report(ref ok, "监护码对时服务会把标记抹掉",
                right is null && !File.Exists(sealPath),
                right ?? "抹掉了");

            // 服务那边不该抱怨"收不紧" —— 收不紧只会在它自己试过之后才说
            // （控制台里以普通身份跑时它不试，因为收完它就自己都读不了了）。
            var complained = serviceLog.Any(l => l.Contains("权限没收紧"));
            Report(ref ok, "没有'权限收不紧'的抱怨", !complained,
                complained ? "服务说它没能把权限收成 SYSTEM/管理员" : "没有抱怨");

            return ok ? 0 : 1;
        }
        finally
        {
            Kill(service);
        }
    }

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

    private static Process StartService(string exe, string configPath, out List<string> log)
    {
        var collected = new List<string>();
        log = collected;

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

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (collected) collected.Add(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (collected) collected.Add(e.Data); };
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
}
