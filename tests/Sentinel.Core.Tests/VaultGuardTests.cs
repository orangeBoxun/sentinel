using System.IO;
using Sentinel.Core.Vault;
using Xunit;

namespace Sentinel.Core.Tests;

public class VaultGuardTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "sentinel-guard-" + Guid.NewGuid().ToString("N"));

    public VaultGuardTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch
        {
            // Leftover temp directory is not worth failing over.
        }
    }

    private string Config => Path.Combine(_dir, "config.json");

    /// <summary>Long enough that every record below was written during the current boot session.</summary>
    private static readonly TimeSpan LongUptime = TimeSpan.FromHours(6);

    // ------------------------------------------------------------------ 存活记录

    [Fact]
    public void The_liveness_record_round_trips()
    {
        var when = DateTimeOffset.UtcNow;
        VaultGuard.WriteAlive(Config, "运行中", when);

        var read = VaultGuard.ReadAlive(Config);

        Assert.NotNull(read);
        Assert.True((read!.Value - when).Duration() < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void A_missing_or_corrupt_record_reads_as_absent()
    {
        Assert.Null(VaultGuard.ReadAlive(Config));

        File.WriteAllText(VaultGuard.AlivePathFor(Config), "{ not json");
        Assert.Null(VaultGuard.ReadAlive(Config));
    }

    // ------------------------------------------------------------------ 服务自查

    [Fact]
    public void A_first_start_has_nothing_to_compare_against()
    {
        Assert.Null(VaultGuard.DetectStoppage(Config, DateTimeOffset.Now, LongUptime));
    }

    [Fact]
    public void A_long_gap_during_one_boot_is_a_stoppage()
    {
        var now = DateTimeOffset.UtcNow;

        // Last alive half an hour ago, and the machine has been up for six hours: the Service was
        // gone while the machine was running. That is the only thing this can mean.
        VaultGuard.WriteAlive(Config, "运行中", now - TimeSpan.FromMinutes(30));

        var reason = VaultGuard.DetectStoppage(Config, now, LongUptime);

        Assert.NotNull(reason);
        Assert.Contains("中断", reason);
    }

    [Fact]
    public void A_gap_that_the_machine_was_off_for_is_not_a_stoppage()
    {
        var now = DateTimeOffset.UtcNow;

        // The record is from before this boot, so the machine was off in between. A reboot is not
        // a dismantling, and treating it as one would seal a vault on every restart.
        VaultGuard.WriteAlive(Config, "运行中", now - TimeSpan.FromHours(8));

        Assert.Null(VaultGuard.DetectStoppage(Config, now, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void A_short_gap_is_a_crash_not_a_stoppage()
    {
        var now = DateTimeOffset.UtcNow;

        // Five seconds is the failure recovery bringing a crashed Service straight back. Sealing
        // for that would mean every crash cost a trip to whoever holds the guardian code.
        VaultGuard.WriteAlive(Config, "运行中", now - TimeSpan.FromSeconds(5));

        Assert.Null(VaultGuard.DetectStoppage(Config, now, LongUptime));
    }

    [Fact]
    public void The_threshold_is_the_line_between_the_two()
    {
        var now = DateTimeOffset.UtcNow;

        VaultGuard.WriteAlive(Config, "运行中", now - VaultGuard.StoppageThreshold + TimeSpan.FromMinutes(1));
        Assert.Null(VaultGuard.DetectStoppage(Config, now, LongUptime));

        VaultGuard.WriteAlive(Config, "运行中", now - VaultGuard.StoppageThreshold - TimeSpan.FromMinutes(1));
        Assert.NotNull(VaultGuard.DetectStoppage(Config, now, LongUptime));
    }

    // ------------------------------------------------------------------ 封印

    [Fact]
    public void Sealing_is_final_and_keeps_the_first_reason()
    {
        Assert.True(VaultGuard.Seal(Config, "服务被删了"));
        Assert.False(VaultGuard.Seal(Config, "后来又发现别的"));

        Assert.Equal("服务被删了", VaultGuard.ReadSeal(Config));
    }

    [Fact]
    public void A_seal_outranks_everything_else()
    {
        VaultGuard.Seal(Config, "配置被换掉了");
        VaultGuard.WriteAlive(Config, "运行中", DateTimeOffset.UtcNow);

        // Restoring the protection does not reopen the door: that is the entire point of sealing,
        // and the reason the user's password stops working until the guardian is involved.
        Assert.Equal("配置被换掉了", VaultGuard.ReadSeal(Config));
    }

    [Fact]
    public void A_seal_written_by_the_uninstaller_is_understood()
    {
        // Exactly what installer/uninstall.ps1's ConvertTo-Json produces. That script writes the
        // file itself rather than calling the installed exe — an old build would ignore --seal-vault
        // and hang the console — so the two sides agree by convention, and this test is what keeps
        // them agreeing.
        File.WriteAllText(VaultGuard.SealPathFor(Config), """
            {
                "Reason":  "保险箱：Sentinel 被卸载了",
                "At":  "2026-10-10 12:00:00"
            }
            """);

        Assert.Equal("保险箱：Sentinel 被卸载了", VaultGuard.ReadSeal(Config));
    }

    [Fact]
    public void An_unreadable_seal_still_counts_as_sealed()
    {
        File.WriteAllText(VaultGuard.SealPathFor(Config), "not json at all");

        // Failing open here would make corrupting one file a way to un-seal the vault.
        Assert.Equal("封印状态无法读取", VaultGuard.ReadSeal(Config));
    }

    [Fact]
    public void Removing_the_seal_reopens_the_door()
    {
        VaultGuard.Seal(Config, "测试");
        VaultGuard.ClearSeal(Config);

        Assert.Null(VaultGuard.ReadSeal(Config));
    }

    // ------------------------------------------------------------------ 别的

    [Fact]
    public void Maintenance_suspends_the_watchdog()
    {
        Assert.False(VaultGuard.InMaintenance(Config));

        File.WriteAllText(VaultGuard.MaintenancePathFor(Config), "");
        Assert.True(VaultGuard.InMaintenance(Config));
    }

    [Fact]
    public void A_vault_that_was_never_set_up_is_not_a_vault()
    {
        Assert.False(VaultGuard.VaultExists(Config));

        Directory.CreateDirectory(VaultGuard.VaultRootFor(Config));
        File.WriteAllText(VaultGuard.KeysPathFor(Config), "{}");

        Assert.True(VaultGuard.VaultExists(Config));
    }

    [Fact]
    public void The_guard_files_sit_beside_the_config()
    {
        // Not under vault/: the whole mechanism depends on these living where a standard user
        // cannot write, and vault/ has to stay writable for the tray app to put files in it.
        Assert.Equal(_dir, VaultGuard.DirectoryFor(Config));
        Assert.Equal(_dir, Path.GetDirectoryName(VaultGuard.SealPathFor(Config)));
        Assert.Equal(_dir, Path.GetDirectoryName(VaultGuard.AlivePathFor(Config)));

        Directory.CreateDirectory(VaultGuard.VaultRootFor(Config));
        Assert.NotEqual(VaultGuard.DirectoryFor(Config), VaultGuard.VaultRootFor(Config));
    }
}
