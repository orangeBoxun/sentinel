using System.IO;
using Sentinel.Core.Vault;
using Xunit;

namespace Sentinel.Core.Tests;

public class VaultTests : IDisposable
{
    /// <summary>Real cost is 600k; the tests here are about behaviour, not about guessing cost.</summary>
    private const int Fast = 1_000;

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "sentinel-vault-" + Guid.NewGuid().ToString("N"));

    public VaultTests() => Directory.CreateDirectory(_dir);

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

    private string KeysPath => Path.Combine(_dir, "keys.json");

    /// <summary>测试里的"这台机器"。绑定的门要用它 —— 换个值就等于换了一台机器。</summary>
    private static readonly byte[] Secret = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    private static readonly byte[] AnotherMachine = Enumerable.Range(101, 32).Select(i => (byte)i).ToArray();

    // ------------------------------------------------------------------ 监护码

    [Fact]
    public void A_guardian_code_survives_being_typed_back()
    {
        var code = VaultCrypto.NewGuardianCode();

        // What someone copying it onto paper and typing it later actually produces.
        var retyped = code.ToLowerInvariant()
            .Replace('-', ' ')
            .Replace('0', 'o')
            .Replace('1', 'i');

        Assert.Equal(
            VaultCrypto.NormalizeGuardianCode(code),
            VaultCrypto.NormalizeGuardianCode(retyped));
    }

    [Fact]
    public void A_mistyped_guardian_code_is_rejected()
    {
        var code = VaultCrypto.NewGuardianCode();
        var compact = VaultCrypto.NormalizeGuardianCode(code)!;

        // One character changed, to another character that is perfectly valid in this alphabet.
        var wrong = (compact[0] == '2' ? '3' : '2') + compact[1..];

        Assert.Null(VaultCrypto.NormalizeGuardianCode(wrong));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("SHORT")]
    [InlineData("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!")]
    public void Nonsense_is_not_a_guardian_code(string text) =>
        Assert.Null(VaultCrypto.NormalizeGuardianCode(text));

    [Fact]
    public void Two_codes_are_never_the_same()
    {
        Assert.NotEqual(VaultCrypto.NewGuardianCode(), VaultCrypto.NewGuardianCode());
    }

    // ------------------------------------------------------------------ 密码A

    [Fact]
    public void The_users_password_opens_the_vault()
    {
        var code = VaultCrypto.NewGuardianCode();
        var keys = VaultKeys.Create("很长的密码A-示例", code, Secret, Fast);

        var key = keys.UnlockWithUserPassword("很长的密码A-示例", Secret);
        Assert.NotNull(key);
        Assert.Equal(VaultCrypto.KeySize, key!.Length);
    }

    [Fact]
    public void A_wrong_password_opens_nothing()
    {
        var keys = VaultKeys.Create("right", VaultCrypto.NewGuardianCode(), Secret, Fast);
        Assert.Null(keys.UnlockWithUserPassword("wrong", Secret));
    }

    [Fact]
    public void The_two_doors_reach_the_same_key()
    {
        var code = VaultCrypto.NewGuardianCode();
        var keys = VaultKeys.Create("密码A", code, Secret, Fast);

        Assert.Equal(keys.UnlockWithUserPassword("密码A", Secret), keys.UnlockWithGuardianCode(code));
    }

    // ------------------------------------------------------------------ 换锁

    [Fact]
    public void Sealing_closes_the_users_door_and_leaves_the_guardians()
    {
        var code = VaultCrypto.NewGuardianCode();
        var keys = VaultKeys.Create("密码A", code, Secret, Fast);
        var before = keys.UnlockWithUserPassword("密码A", Secret);

        Assert.True(keys.SealToGuardian("测试", DateTimeOffset.Now));

        Assert.Equal(VaultState.SealedToGuardian, keys.State);
        Assert.Null(keys.UnlockWithUserPassword("密码A", Secret));
        Assert.False(keys.UserDoorAvailable);

        // The whole point: the same data key is still reachable, so nothing was lost.
        Assert.Equal(before, keys.UnlockWithGuardianCode(code));
    }

    [Fact]
    public void Sealing_twice_is_not_an_error()
    {
        var keys = VaultKeys.Create("密码A", VaultCrypto.NewGuardianCode(), Secret, Fast);

        Assert.True(keys.SealToGuardian("第一次", DateTimeOffset.Now));
        Assert.False(keys.SealToGuardian("第二次", DateTimeOffset.Now));
        Assert.Equal("第一次", keys.SealedReason);
    }

    [Fact]
    public void Sealing_discards_the_users_wrapping_entirely()
    {
        var keys = VaultKeys.Create("密码A", VaultCrypto.NewGuardianCode(), Secret, Fast);
        keys.SealToGuardian("测试", DateTimeOffset.Now);

        // Not just flagged as closed — the wrapping is gone, so restoring the protection does not
        // quietly restore the door along with it.
        Assert.Null(keys.WrappedByUser);
        Assert.Null(keys.UserSalt);
    }

    [Fact]
    public void A_wrong_guardian_code_opens_nothing()
    {
        var keys = VaultKeys.Create("密码A", VaultCrypto.NewGuardianCode(), Secret, Fast);
        keys.SealToGuardian("测试", DateTimeOffset.Now);

        Assert.Null(keys.UnlockWithGuardianCode(VaultCrypto.NewGuardianCode()));
    }

    // ------------------------------------------------------------------ 落盘

    [Fact]
    public void Sealing_survives_a_restart()
    {
        var code = VaultCrypto.NewGuardianCode();
        var keys = VaultKeys.Create("密码A", code, Secret, Fast);
        var expected = keys.UnlockWithUserPassword("密码A", Secret);

        keys.SealToGuardian("服务配置被改", DateTimeOffset.Now);
        keys.Save(KeysPath);

        var reloaded = VaultKeys.Load(KeysPath)!;

        Assert.Equal(VaultState.SealedToGuardian, reloaded.State);
        Assert.Equal("服务配置被改", reloaded.SealedReason);
        Assert.Null(reloaded.UnlockWithUserPassword("密码A", Secret));
        Assert.Equal(expected, reloaded.UnlockWithGuardianCode(code));
    }

    [Fact]
    public void A_missing_or_corrupt_key_file_reads_as_absent_rather_than_throwing()
    {
        Assert.Null(VaultKeys.Load(KeysPath));

        File.WriteAllText(KeysPath, "{ not json");
        Assert.Null(VaultKeys.Load(KeysPath));
    }

    [Fact]
    public void A_tampered_wrapping_is_refused_rather_than_throwing()
    {
        var keys = VaultKeys.Create("密码A", VaultCrypto.NewGuardianCode(), Secret, Fast);
        var safe = keys.WrappedByUser!.ToArray();
        safe[^1] ^= 0xFF;
        keys.WrappedByUser = safe;

        Assert.Null(keys.UnlockWithUserPassword("密码A", Secret));
    }

    [Fact]
    public void Creating_a_vault_needs_a_valid_guardian_code()
    {
        // Refusing here is the point: a vault whose second door was never set up would have nothing
        // to fall back on, and the seal would be theatre.
        Assert.Throws<ArgumentException>(() => VaultKeys.Create("密码A", "not-a-code", Secret, Fast));
    }

    // ------------------------------------------------------------------ 绑机器

    /// <summary>
    /// 密码那道门绑在这台机器的秘密上：**换台机器（或者拿不到那份秘密）就打不开**。
    /// 这是"封印之前拷出去的 vault 副本，封印之后就作废"的全部道理所在。
    /// </summary>
    [Fact]
    public void The_password_door_needs_this_machines_secret()
    {
        var code = VaultCrypto.NewGuardianCode();
        var keys = VaultKeys.Create("密码A", code, Secret, Fast);

        Assert.Equal(keys.UnlockWithUserPassword("密码A", Secret), keys.UnlockWithGuardianCode(code));

        // 换了台机器
        Assert.Null(keys.UnlockWithUserPassword("密码A", AnotherMachine));

        // 或者那份秘密根本没有（服务没在跑）
        Assert.Null(keys.UnlockWithUserPassword("密码A"));
    }

    /// <summary>监护码那道门**不绑机器** —— 它存在的意义就是"机器没了、重装了还能回来"。</summary>
    [Fact]
    public void The_guardian_door_does_not_need_the_machine()
    {
        var code = VaultCrypto.NewGuardianCode();
        var keys = VaultKeys.Create("密码A", code, Secret, Fast);

        Assert.NotNull(keys.UnlockWithGuardianCode(code));
        Assert.False(keys.MachineBound is false);   // 新文件一律是绑定的
    }

    /// <summary>
    /// 老文件（绑定之前建的）照旧能用密码打开，而且第一次打开时会被就地升级成绑定的 ——
    /// 内容一个字节都不动。
    /// </summary>
    [Fact]
    public void An_older_unbound_door_still_opens_and_gets_upgraded()
    {
        var code = VaultCrypto.NewGuardianCode();
        var key = VaultCrypto.NewKey();

        // 造一个"老格式"的文件：只用密码派生。
        var salt = VaultCrypto.NewSalt();
        var legacy = new VaultKeys
        {
            Iterations = Fast,
            UserSalt = salt,
            WrappedByUser = VaultCrypto.Wrap(key, VaultCrypto.DeriveKey("密码A", salt, Fast)),
            GuardianSalt = VaultCrypto.NewSalt(),
            MachineBound = false,
        };

        legacy.GuardianSalt = VaultCrypto.NewSalt();
        legacy.WrappedByGuardian = VaultCrypto.Wrap(
            key, VaultCrypto.DeriveKey(VaultCrypto.NormalizeGuardianCode(code)!, legacy.GuardianSalt, Fast));

        // 老门不需要那份秘密
        Assert.Equal(key, legacy.UnlockWithUserPassword("密码A"));

        legacy.BindToMachine(key, "密码A", Secret);

        Assert.True(legacy.MachineBound);
        Assert.Equal(key, legacy.UnlockWithUserPassword("密码A", Secret));
        Assert.Null(legacy.UnlockWithUserPassword("密码A"));            // 升级之后就要秘密了
        Assert.Equal(key, legacy.UnlockWithGuardianCode(code));          // 监护码那道门没动
    }

    /// <summary>封印会把本机秘密一起销毁 —— 那之后旧的 vault 副本就只认监护码了。</summary>
    [Fact]
    public void Sealing_destroys_the_machine_secret()
    {
        var config = Path.Combine(_dir, "封印", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);

        var secret = VaultGuard.LoadOrCreateMachineSecret(config);
        Assert.Equal(32, secret.Length);
        Assert.True(File.Exists(VaultGuard.MachineSecretPathFor(config)));

        // 造一个保险箱（封印只对"真的有保险箱"的机器生效）
        Directory.CreateDirectory(VaultGuard.VaultRootFor(config));
        VaultKeys.Create("密码A", VaultCrypto.NewGuardianCode(), secret, Fast)
            .Save(VaultGuard.KeysPathFor(config));

        VaultGuard.SealVault(config, "测试：防护被拆了");

        Assert.False(File.Exists(VaultGuard.MachineSecretPathFor(config)));
        Assert.False(VaultGuard.DestroyMachineSecret(config) is false && File.Exists(VaultGuard.MachineSecretPathFor(config)));
    }
}
