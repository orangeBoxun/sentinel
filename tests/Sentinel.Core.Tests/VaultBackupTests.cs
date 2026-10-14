using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Sentinel.Core.Vault;
using Xunit;

namespace Sentinel.Core.Tests;

public class VaultBackupTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "sentinel-backup-" + Guid.NewGuid().ToString("N"));

    private const string Password = "这是一个足够长的密码";

    public VaultBackupTests() => Directory.CreateDirectory(_dir);

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

    /// <summary>Stands in for one machine: its own config path, and so its own vault directory.</summary>
    private string Machine(string name) => Path.Combine(_dir, name, "config.json");

    /// <summary>测试里的"这台机器"。绑定的门要用它 —— 换个值就等于换了一台机器。</summary>
    private static readonly byte[] Secret = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    private string BackupPath(string name) => Path.Combine(_dir, name + VaultBackup.Extension);

    /// <summary>A file outside any vault, for importing from.</summary>
    private string ScratchFile(string name, string content)
    {
        var path = Path.Combine(_dir, "scratch", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Makes a vault on <paramref name="machine"/> holding a couple of files.</summary>
    private (string Password, string Code) Seed(string machine)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(machine)!);

        var password = Password;
        var code = VaultCrypto.NewGuardianCode();

        using var session = new VaultSession(machine, null, () => Secret);
        Assert.True(session.Create(password, code).Ok);

        var source = Path.Combine(_dir, "source");
        Directory.CreateDirectory(source);

        File.WriteAllText(Path.Combine(source, "one.txt"), "第一个文件");
        File.WriteAllText(Path.Combine(source, "two.txt"), "第二个文件");

        Assert.True(session.Import(Path.Combine(source, "one.txt"), "甲/one.txt").Ok);
        Assert.True(session.Import(Path.Combine(source, "two.txt"), "two.txt").Ok);

        return (password, code);
    }

    // ------------------------------------------------------------------ 往返

    [Fact]
    public void A_backup_restores_onto_another_machine()
    {
        var (_, code) = Seed(Machine("甲机"));
        var backup = BackupPath("备份");

        Assert.True(VaultBackup.Export(Machine("甲机"), backup).Ok);
        Assert.True(File.Exists(backup));

        // The machine the backup is carried to has no guard heartbeat of its own, so the guard is
        // not consulted here — this is about whether the bytes came back.
        var result = VaultBackup.Restore(Machine("乙机"), backup);
        Assert.True(result.Ok, result.Error);

        var keys = VaultKeys.Load(VaultGuard.KeysPathFor(Machine("乙机")));
        Assert.NotNull(keys);

        // **备份里不带密码那道门**：正因为如此，导回来之后密码开不了、只能拿监护码开，
        // 然后重设一个密码。这就是"备份不能用来撤销封印"的代价，也是它的意义。
        Assert.Null(keys!.UnlockWithUserPassword(Password));
        Assert.NotNull(keys.UnlockWithGuardianCode(code));

        using var session = new VaultSession(Machine("乙机"), null, () => Secret);
        Assert.True(session.UnlockWithGuardianCode(code).Ok);
        Assert.Equal(2, session.Entries.Count);

        var output = Path.Combine(_dir, "out");
        Assert.True(session.Extract("甲/one.txt", output).Ok);
        Assert.Equal("第一个文件", File.ReadAllText(Path.Combine(output, "one.txt")));
    }

    [Fact]
    public void Exporting_a_sealed_vault_produces_a_sealed_backup()
    {
        var (password, code) = Seed(Machine("甲机"));
        VaultGuard.SealVault(Machine("甲机"), "测试：密封后导出");

        var backup = BackupPath("密封备份");
        Assert.True(VaultBackup.Export(Machine("甲机"), backup).Ok);
        Assert.True(VaultBackup.Restore(Machine("乙机"), backup).Ok);

        var keys = VaultKeys.Load(VaultGuard.KeysPathFor(Machine("乙机")))!;

        // The password door was gone when the backup was taken, so it is gone in the backup too.
        Assert.Null(keys.UnlockWithUserPassword(password));
        Assert.NotNull(keys.UnlockWithGuardianCode(code));
    }

    [Fact]
    public void Restoring_does_not_lift_a_seal()
    {
        var (password, _) = Seed(Machine("甲机"));
        var backup = BackupPath("备份");
        Assert.True(VaultBackup.Export(Machine("甲机"), backup).Ok);

        VaultGuard.SealVault(Machine("甲机"), "测试");
        Assert.True(VaultBackup.Restore(Machine("甲机"), backup).Ok);

        // The seal lives beside the config, not in the vault, so putting the vault back does not put
        // the door back. This is what makes a backup safe to keep.
        Assert.NotNull(VaultGuard.ReadSeal(Machine("甲机")));

        using var session = new VaultSession(Machine("甲机"), null, () => Secret);
        Assert.True(session.IsSealed);
        Assert.False(session.UnlockWithPassword(password).Ok);
    }

    // ------------------------------------------------------------------ 坏备份

    [Fact]
    public void A_damaged_backup_is_refused_and_the_existing_vault_is_untouched()
    {
        Seed(Machine("甲机"));
        var backup = BackupPath("备份");
        Assert.True(VaultBackup.Export(Machine("甲机"), backup).Ok);

        var before = Snapshot(Machine("甲机"));

        // Corrupt the archive itself. Whether that surfaces as a broken zip or a hash mismatch does
        // not matter; what matters is that the vault that still works is not touched.
        var bytes = File.ReadAllBytes(backup);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(backup, bytes);

        var result = VaultBackup.Restore(Machine("甲机"), backup);

        Assert.False(result.Ok);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));

        var after = Snapshot(Machine("甲机"));
        Assert.Equal(before.Keys.OrderBy(k => k), after.Keys.OrderBy(k => k));
        foreach (var name in before.Keys)
            Assert.Equal(before[name], after[name]);
    }

    [Fact]
    public void A_backup_whose_contents_do_not_match_its_manifest_is_refused()
    {
        Seed(Machine("甲机"));

        // A well-formed archive whose manifest claims a hash the file does not have — the check that
        // has to run *before* anything is written.
        var tampered = BackupPath("对不上");
        using (var zip = ZipFile.Open(tampered, ZipArchiveMode.Create))
        {
            var manifest = new BackupManifest(1, DateTimeOffset.Now.ToString("O"),
                [new BackupFile("keys.json", new string('0', 64), 5)]);

            using (var stream = zip.CreateEntry("backup.json").Open())
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                writer.Write(JsonSerializer.Serialize(manifest));

            using var entry = zip.CreateEntry("vault/keys.json").Open();
            entry.Write(Encoding.UTF8.GetBytes("假的"));
        }

        var before = Snapshot(Machine("甲机"));
        var result = VaultBackup.Restore(Machine("甲机"), tampered);

        Assert.False(result.Ok);
        Assert.Contains("损坏", result.Error);
        Assert.Equal(before.Keys.OrderBy(k => k), Snapshot(Machine("甲机")).Keys.OrderBy(k => k));
    }

    private Dictionary<string, byte[]> Snapshot(string machine) =>
        Directory.GetFiles(VaultGuard.VaultRootFor(machine), "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetFileName(path).Equals("export.json", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes);

    [Fact]
    public void A_manifest_that_tries_to_escape_the_vault_is_refused()
    {
        Seed(Machine("甲机"));

        // A backup is a file that arrives from elsewhere, so its own list of what to write is not
        // trusted. This one claims a path outside the vault directory.
        var hostile = BackupPath("恶意");
        var payload = Encoding.UTF8.GetBytes("pwned");

        using (var zip = ZipFile.Open(hostile, ZipArchiveMode.Create))
        {
            var manifest = new BackupManifest(1, DateTimeOffset.Now.ToString("O"),
                [new BackupFile("../../escaped.txt", Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(payload)).ToLowerInvariant(), payload.Length)]);

            using (var stream = zip.CreateEntry("backup.json").Open())
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                writer.Write(JsonSerializer.Serialize(manifest));

            using var entry = zip.CreateEntry("vault/../../escaped.txt").Open();
            entry.Write(payload);
        }

        var result = VaultBackup.Restore(Machine("甲机"), hostile);

        Assert.False(result.Ok);
        Assert.False(File.Exists(Path.Combine(_dir, "escaped.txt")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_dir)!, "escaped.txt")));
    }

    [Fact]
    public void Something_that_is_not_a_backup_is_refused()
    {
        Seed(Machine("甲机"));

        var notABackup = BackupPath("随便");
        File.WriteAllText(notABackup, "这不是压缩包");

        Assert.False(VaultBackup.Restore(Machine("甲机"), notABackup).Ok);

        // A valid zip that simply is not ours.
        var zipButNotOurs = BackupPath("外来的");
        using (var zip = ZipFile.Open(zipButNotOurs, ZipArchiveMode.Create))
            zip.CreateEntry("hello.txt");

        var result = VaultBackup.Restore(Machine("甲机"), zipButNotOurs);
        Assert.False(result.Ok);
        Assert.Contains("backup.json", result.Error);
    }

    // ------------------------------------------------------------------ 换掉旧的

    [Fact]
    public void Restoring_really_puts_the_backups_content_back()
    {
        var (_, code) = Seed(Machine("甲机"));              // two files
        var backup = BackupPath("备份");
        Assert.True(VaultBackup.Export(Machine("甲机"), backup).Ok);

        // Add a third after the backup was taken, so the live vault and the backup differ.
        using (var session = new VaultSession(Machine("甲机"), null, () => Secret))
        {
            Assert.True(session.UnlockWithPassword(Password).Ok);
            Assert.True(session.Import(ScratchFile("three.txt", "后来加的"), "three.txt").Ok);
            Assert.Equal(3, session.Entries.Count);
        }

        Assert.True(VaultBackup.Restore(Machine("甲机"), backup).Ok);

        // 钥匙文件也被备份里那份替换了 —— 而备份不带密码门，所以这里用监护码开。
        using var restored = new VaultSession(Machine("甲机"), null, () => Secret);
        Assert.True(restored.UnlockWithGuardianCode(code).Ok);

        // Back to what the backup held: the file added afterwards is gone.
        Assert.Equal(2, restored.Entries.Count);
        Assert.DoesNotContain(restored.Entries, e => e.Path == "three.txt");
    }

    [Fact]
    public void A_backup_does_not_carry_the_password_door()
    {
        var original = VaultCrypto.NewGuardianCode();
        var fresh = VaultCrypto.NewGuardianCode();

        Directory.CreateDirectory(Path.GetDirectoryName(Machine("甲机"))!);

        using (var session = new VaultSession(Machine("甲机"), null, () => Secret))
        {
            Assert.True(session.Create(Password, original).Ok);
            Assert.True(session.Import(ScratchFile("a.txt", "x"), "a.txt").Ok);
        }

        var backup = BackupPath("备份");
        Assert.True(VaultBackup.Export(Machine("甲机"), backup).Ok);

        // Change both, after the backup was taken.
        using (var session = new VaultSession(Machine("甲机"), null, () => Secret))
        {
            Assert.True(session.UnlockWithPassword(Password).Ok);
            Assert.True(session.ChangePassword("另一个足够长的密码").Ok);
            Assert.True(session.ChangeGuardianCode(original, fresh).Ok);
        }

        Assert.True(VaultBackup.Restore(Machine("甲机"), backup).Ok);

        var keys = VaultKeys.Load(VaultGuard.KeysPathFor(Machine("甲机")))!;

        // 备份是快照，监护码那道门照旧跟着走（所以恢复之后原来的监护码还能开、
        // 后来换掉的不能）。但**密码那道门不跟着走** —— 带上它就等于造了一个
        // 随时能撤销封印的副本。所以恢复之后只能用监护码开，然后重设密码。
        Assert.Null(keys.UnlockWithUserPassword(Password));
        Assert.Null(keys.UnlockWithUserPassword("另一个足够长的密码"));

        Assert.NotNull(keys.UnlockWithGuardianCode(original));
        Assert.Null(keys.UnlockWithGuardianCode(fresh));

        // 而且它自己说得清为什么："这个保险箱已经用不了了，只能拿监护码打开。"
        using var restored = new VaultSession(Machine("甲机"), null, () => Secret);
        var refused = restored.UnlockWithPassword(Password);
        Assert.False(refused.Ok);
        Assert.Contains("监护码", refused.Error ?? "");
    }

    /// <summary>
    /// 直接拆开备份看那份钥匙：**不能有密码那道门**，监护码那道门要在。
    /// 这是"备份不能用来撤销封印"最直接的一条证据。
    /// </summary>
    [Fact]
    public void The_keys_inside_a_backup_have_no_password_door()
    {
        Seed(Machine("甲机"));

        var backup = BackupPath("备份");
        Assert.True(VaultBackup.Export(Machine("甲机"), backup).Ok);

        using var zip = System.IO.Compression.ZipFile.OpenRead(backup);
        var entry = zip.GetEntry("vault/keys.json");

        Assert.NotNull(entry);

        using var reader = new StreamReader(entry!.Open());
        var json = reader.ReadToEnd();

        Assert.DoesNotContain("WrappedByUser", json);
        Assert.DoesNotContain("UserSalt", json);
        Assert.Contains("WrappedByGuardian", json);
        Assert.Contains("SealedToGuardian", json);
    }

    [Fact]
    public void Restoring_never_touches_anything_outside_the_vault()
    {
        Seed(Machine("甲机"));
        var backup = BackupPath("备份");
        Assert.True(VaultBackup.Export(Machine("甲机"), backup).Ok);

        var parent = Path.GetDirectoryName(VaultGuard.VaultRootFor(Machine("甲机")))!;
        var before = Directory.GetFileSystemEntries(parent).OrderBy(x => x).ToArray();

        Assert.True(VaultBackup.Restore(Machine("甲机"), backup).Ok);

        // The vault sits in a directory the user cannot write — only vault\ itself is writable.
        // Creating a sibling of it, even for a moment, fails with access denied on a real install,
        // and a test running in a temp directory would never notice. This is that notice.
        var after = Directory.GetFileSystemEntries(parent).OrderBy(x => x).ToArray();
        Assert.Equal(before, after);
    }

    [Fact]
    public void Exporting_with_no_vault_says_so()
    {
        var machine = Machine("空机");
        Directory.CreateDirectory(Path.GetDirectoryName(machine)!);

        var result = VaultBackup.Export(machine, BackupPath("无"));
        Assert.False(result.Ok);
        Assert.Contains("还没有保险箱", result.Error);
    }

    // ------------------------------------------------------------------ 提醒

    [Fact]
    public void The_export_date_is_remembered_and_reset_by_a_restore()
    {
        Seed(Machine("甲机"));
        Assert.Null(VaultBackup.LastExport(Machine("甲机")));

        var backup = BackupPath("备份");
        var when = DateTimeOffset.Now;
        Assert.True(VaultBackup.Export(Machine("甲机"), backup, when).Ok);

        var recorded = VaultBackup.LastExport(Machine("甲机"));
        Assert.NotNull(recorded);
        Assert.True((recorded!.Value - when).Duration() < TimeSpan.FromSeconds(5));

        Assert.True(VaultBackup.Restore(Machine("甲机"), backup).Ok);

        // After a restore the live vault came from a backup, so when *that* vault was last exported
        // is not something this machine knows.
        Assert.Null(VaultBackup.LastExport(Machine("甲机")));
    }

    [Fact]
    public void The_suggested_name_sorts_by_date()
    {
        var older = VaultBackup.DefaultName(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
        var newer = VaultBackup.DefaultName(new DateTimeOffset(2026, 11, 12, 3, 4, 5, TimeSpan.Zero));

        Assert.EndsWith(VaultBackup.Extension, older);
        Assert.True(string.CompareOrdinal(older, newer) < 0);
    }
}
