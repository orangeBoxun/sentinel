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
        var keys = VaultKeys.Create("很长的密码A-示例", code, Fast);

        var key = keys.UnlockWithUserPassword("很长的密码A-示例");
        Assert.NotNull(key);
        Assert.Equal(VaultCrypto.KeySize, key!.Length);
    }

    [Fact]
    public void A_wrong_password_opens_nothing()
    {
        var keys = VaultKeys.Create("right", VaultCrypto.NewGuardianCode(), Fast);
        Assert.Null(keys.UnlockWithUserPassword("wrong"));
    }

    [Fact]
    public void The_two_doors_reach_the_same_key()
    {
        var code = VaultCrypto.NewGuardianCode();
        var keys = VaultKeys.Create("密码A", code, Fast);

        Assert.Equal(keys.UnlockWithUserPassword("密码A"), keys.UnlockWithGuardianCode(code));
    }

    // ------------------------------------------------------------------ 换锁

    [Fact]
    public void Sealing_closes_the_users_door_and_leaves_the_guardians()
    {
        var code = VaultCrypto.NewGuardianCode();
        var keys = VaultKeys.Create("密码A", code, Fast);
        var before = keys.UnlockWithUserPassword("密码A");

        Assert.True(keys.SealToGuardian("测试", DateTimeOffset.Now));

        Assert.Equal(VaultState.SealedToGuardian, keys.State);
        Assert.Null(keys.UnlockWithUserPassword("密码A"));
        Assert.False(keys.UserDoorAvailable);

        // The whole point: the same data key is still reachable, so nothing was lost.
        Assert.Equal(before, keys.UnlockWithGuardianCode(code));
    }

    [Fact]
    public void Sealing_twice_is_not_an_error()
    {
        var keys = VaultKeys.Create("密码A", VaultCrypto.NewGuardianCode(), Fast);

        Assert.True(keys.SealToGuardian("第一次", DateTimeOffset.Now));
        Assert.False(keys.SealToGuardian("第二次", DateTimeOffset.Now));
        Assert.Equal("第一次", keys.SealedReason);
    }

    [Fact]
    public void Sealing_discards_the_users_wrapping_entirely()
    {
        var keys = VaultKeys.Create("密码A", VaultCrypto.NewGuardianCode(), Fast);
        keys.SealToGuardian("测试", DateTimeOffset.Now);

        // Not just flagged as closed — the wrapping is gone, so restoring the protection does not
        // quietly restore the door along with it.
        Assert.Null(keys.WrappedByUser);
        Assert.Null(keys.UserSalt);
    }

    [Fact]
    public void A_wrong_guardian_code_opens_nothing()
    {
        var keys = VaultKeys.Create("密码A", VaultCrypto.NewGuardianCode(), Fast);
        keys.SealToGuardian("测试", DateTimeOffset.Now);

        Assert.Null(keys.UnlockWithGuardianCode(VaultCrypto.NewGuardianCode()));
    }

    // ------------------------------------------------------------------ 落盘

    [Fact]
    public void Sealing_survives_a_restart()
    {
        var code = VaultCrypto.NewGuardianCode();
        var keys = VaultKeys.Create("密码A", code, Fast);
        var expected = keys.UnlockWithUserPassword("密码A");

        keys.SealToGuardian("服务配置被改", DateTimeOffset.Now);
        keys.Save(KeysPath);

        var reloaded = VaultKeys.Load(KeysPath)!;

        Assert.Equal(VaultState.SealedToGuardian, reloaded.State);
        Assert.Equal("服务配置被改", reloaded.SealedReason);
        Assert.Null(reloaded.UnlockWithUserPassword("密码A"));
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
        var keys = VaultKeys.Create("密码A", VaultCrypto.NewGuardianCode(), Fast);
        var safe = keys.WrappedByUser!.ToArray();
        safe[^1] ^= 0xFF;
        keys.WrappedByUser = safe;

        Assert.Null(keys.UnlockWithUserPassword("密码A"));
    }

    [Fact]
    public void Creating_a_vault_needs_a_valid_guardian_code()
    {
        // Refusing here is the point: a vault whose second door was never set up would have nothing
        // to fall back on, and the seal would be theatre.
        Assert.Throws<ArgumentException>(() => VaultKeys.Create("密码A", "not-a-code", Fast));
    }
}
