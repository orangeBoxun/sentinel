using System.Diagnostics;
using Sentinel.Core.Config;
using Sentinel.Core.Vault;
using Sentinel.Platform;

namespace Sentinel.Service;

/// <summary>
/// Runs the watchdog checks. These are not service code paths: the executable is reused as a small
/// tool so that the scheduled task and the installer both invoke exactly the same logic, with no
/// path-quoting to get wrong in between.
/// </summary>
internal static class Watchdog
{
    /// <summary>Name of the service being watched. Must match what install.ps1 registers.</summary>
    public const string ServiceName = "Sentinel";

    /// <summary>
    /// Service permissions with SERVICE_STOP (WP) withheld from Administrators.
    ///
    /// This is what makes "just stop it" impossible. Stopping through the Services console, sc stop,
    /// or Task Manager's services tab is refused; the only way left is to kill the process outright,
    /// which is a crash — and the service's own failure recovery brings it back in five seconds.
    ///
    /// Administrators keep WRITE_DAC (WD), so the way out is a deliberate <c>sc sdset</c> to loosen
    /// this first. That is the point: the escape exists, but it is a sequence of deliberate commands
    /// rather than one click, and the watchdog re-applies this every minute.
    /// </summary>
    private const string HardenedSddl =
        "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)";

    /// <summary>The permissions Windows gives a service by default; used again on uninstall.</summary>
    private const string DefaultSddl =
        "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)";

    /// <summary>
    /// What the scheduled task runs, once a minute: put the permissions back, then make sure the
    /// service is up.
    ///
    /// This covers what the service's own failure recovery cannot. "Failure" recovery only reacts to
    /// crashes — stopping the service deliberately (Task Manager's End task, `sc stop`, the Services
    /// console) looks like a normal stop, and it then stays down forever. Tightening the permissions
    /// first is what stops that path being available at all.
    /// </summary>
    public static int RunAsWatchdog(string[] args)
    {
        var configPath = ConfigPaths.Resolve(args);

        // 先留一句"我来过"。服务靠它发现**看门狗**被停了 —— 而看门狗自己发现不了自己不存在，
        // 只有服务还在跑的时候才有人能注意到（这条路径的来龙去脉见 VaultGuard 里的说明）。
        //
        // 写在维护检查**之前**：装/卸期间也要留，否则装完之后服务会以为看门狗一直没跑过。
        TouchAlive(configPath);

        // The installer and uninstaller put this in place while they work. An upgrade stops and
        // deletes the service on purpose for a few seconds; without this the watchdog would read
        // that as a dismantling and seal the vault on every install.
        if (VaultGuard.InMaintenance(configPath))
            return 0;

        // Restore the service's own configuration first. "Disable the service and reboot" is
        // otherwise a one-command bypass: a disabled service cannot be started, so the watchdog
        // would find it stopped and be unable to do anything about it.
        if (!RestoreServiceConfiguration(configPath))
        {
            // The service is not registered at all, and putting it back is not something the
            // watchdog can do — it has no idea what the install should look like. That is `sc delete`
            // or a torn-out install, and it is the signal the vault is watching for.
            SealVault(configPath, "Sentinel 服务已经不存在了（被删除或卸载），看门狗无法恢复它。");
            return 0;
        }

        // Starting an already-running service fails harmlessly, and that is the usual case: one
        // process launch a minute to ask a question is cheaper than parsing sc's output to avoid it.
        Run("sc.exe", ["start", ServiceName]);
        return 0;
    }

    /// <summary>
    /// Puts the service definition back the way it should be: auto-start, the right executable, the
    /// failure actions, and the hardened permissions.
    ///
    /// <para>
    /// Every one of these is something an administrator can change with a single command, and each
    /// one disables the protection on its own — so the watchdog re-asserts all of them every minute.
    /// Loosening the permissions is repaired rather than punished: it is undone before it buys
    /// anyone anything, and sealing the vault over a change that was already reversed would cost a
    /// trip to the guardian for nothing.
    /// </para>
    /// </summary>
    /// <returns>False when the service is not registered — a different problem, and not one the watchdog can fix silently.</returns>
    private static bool RestoreServiceConfiguration(string configPath)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable))
            return false;

        var binaryPath = $"\"{executable}\" --config \"{configPath}\"";

        Run("sc.exe", ["config", ServiceName, "start=", "auto", "binPath=", binaryPath]);
        Run("sc.exe", ["failure", ServiceName, "reset=", "0", "actions=", "restart/5000/restart/5000/restart/5000"]);
        Harden();

        return Run("sc.exe", ["query", ServiceName]) == 0;
    }

    /// <summary>
    /// 留一句"我来过"。
    ///
    /// <para>
    /// 写不进去就算了，但**不能因此被误判成"看门狗没了"**：服务那边把"从来没有过这个文件"和
    /// "有、但旧了"分得很开 —— 前者不判（新装、或者装的时候用了 -NoWatchdog），所以权限不对
    /// 导致的写失败不会被当成"看门狗被停掉了"。
    /// </para>
    /// </summary>
    private static void TouchAlive(string configPath)
    {
        try
        {
            File.WriteAllText(
                VaultGuard.WatchdogAlivePathFor(configPath),
                DateTimeOffset.UtcNow.ToString("O"));
        }
        catch
        {
            // 见上：这次失败最多让服务以为看门狗最近没跑，而那不构成封印的理由。
        }
    }

    /// <summary>Seals the vault, if there is one. Safe to call when the protection is fine.</summary>
    public static int SealVault(string configPath, string reason)
    {
        if (!VaultGuard.VaultExists(configPath))
        {
            Console.WriteLine($"（没有保险箱，无需封印：{reason}）");
            return 0;
        }

        Console.WriteLine(VaultGuard.SealVault(configPath, reason)
            ? $"保险箱已封印：{reason}"
            : $"保险箱此前已被封印。（本次：{reason}）");

        return 0;
    }

    /// <summary>Applies the hardened permissions. Called by the installer and by the watchdog.</summary>
    public static int Harden()
    {
        Run("sc.exe", ["sdset", ServiceName, HardenedSddl]);
        return 0;
    }

    /// <summary>Puts the default permissions back so the service can be stopped and removed.</summary>
    public static int Relax()
    {
        Run("sc.exe", ["sdset", ServiceName, DefaultSddl]);
        return 0;
    }

    /// <summary>Registers (or refreshes) the scheduled task. Used by install.ps1 and by the service itself.</summary>
    public static int Ensure(string configPath)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable))
        {
            Console.Error.WriteLine("无法确定可执行文件路径。");
            return 1;
        }

        // The config path goes into the task's command line so the watchdog can restore the service's
        // own binPath rather than guess at it.
        var arguments = $"--watchdog --config \"{configPath}\"";

        if (ScheduledTask.Ensure(ScheduledTask.WatchdogTaskName, executable, arguments, out var error))
        {
            Console.WriteLine($"看门狗计划任务已注册：{ScheduledTask.WatchdogTaskName} 每分钟检查一次");
            return 0;
        }

        Console.Error.WriteLine($"注册看门狗失败：{error}");
        return 1;
    }

    public static int Remove()
    {
        // Idempotent: for an uninstaller, "there was nothing there" is a success.
        if (!ScheduledTask.Exists(ScheduledTask.WatchdogTaskName))
            return 0;

        return ScheduledTask.Delete(ScheduledTask.WatchdogTaskName) ? 0 : 1;
    }

    /// <summary>
    /// True when running as the LocalSystem account — the only identity that can manage the task.
    /// Used to keep console runs (development) from logging a failure every few minutes.
    /// </summary>
    public static bool IsSystem()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return identity.IsSystem;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Runs sc.exe and reports its exit code. The code is the answer for the one question the
    /// watchdog asks that has consequences — "is the service still registered?" — and sc's own text
    /// output is localized, which makes parsing it a bad idea on any machine that is not English.
    /// </summary>
    private static int Run(string file, string[] arguments)
    {
        try
        {
            var info = new ProcessStartInfo(file)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (var argument in arguments)
                info.ArgumentList.Add(argument);

            using var process = Process.Start(info);
            if (process is null)
                return -1;

            // Drain both pipes before waiting: a full buffer would deadlock the child.
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();

            return process.WaitForExit(20_000) ? process.ExitCode : -1;
        }
        catch
        {
            // Nothing here is worth failing over: the next run a minute from now tries again.
            return -1;
        }
    }
}
