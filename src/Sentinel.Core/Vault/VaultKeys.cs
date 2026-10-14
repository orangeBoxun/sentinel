using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sentinel.Core.Vault;

public enum VaultState
{
    /// <summary>Ordinary operation: the user's password opens the vault.</summary>
    Normal,

    /// <summary>
    /// The protection was dismantled. The user's password no longer opens anything — only the
    /// guardian code does.
    ///
    /// This is a state rather than an event on purpose. "Detect tampering, then destroy something"
    /// has to answer "who was watching at that moment?", and the honest answer is that on a machine
    /// someone has just torn the monitoring out of, nobody was. A state is checked whenever the
    /// vault is opened, which is the only moment that matters, and it gives the same answer whether
    /// it was set a minute ago or a year ago.
    /// </summary>
    SealedToGuardian,
}

/// <summary>
/// The vault's key material: one random data key, wrapped twice.
///
/// <para>
/// The data key is what the files are actually encrypted with, and it never changes. What changes
/// is which password can unwrap it. That is what makes sealing instant regardless of how much is in
/// the vault — and it is why sealing is not a deletion: nothing but the wrapping is removed, so the
/// contents come back whole the moment the guardian code is used.
/// </para>
/// <para>
/// Both wrappings live in the same file, including the guardian's. That is safe because the
/// guardian code is 160 random bits generated here: having the wrapped form on the same disk is no
/// help to anyone who does not already have the string, and the string is the thing that was
/// deliberately handed to someone else.
/// </para>
/// </summary>
public sealed class VaultKeys
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public int Version { get; set; } = 1;

    public int Iterations { get; set; } = VaultCrypto.DefaultIterations;

    public byte[]? UserSalt { get; set; }

    public byte[]? WrappedByUser { get; set; }

    public byte[]? GuardianSalt { get; set; }

    public byte[]? WrappedByGuardian { get; set; }

    public VaultState State { get; set; } = VaultState.Normal;

    /// <summary>
    /// 密码那道门是不是**绑在这台机器上**（见 <see cref="VaultCrypto.DeriveBoundKey"/>）。
    ///
    /// <para>
    /// 老文件（这个改动之前建的）是 false：只用密码派生。它们照旧能开，第一次成功打开时会被
    /// 就地升级成绑定的 —— 不声不响地把门换掉，内容一个字节都不动。
    /// </para>
    /// </summary>
    public bool MachineBound { get; set; }

    /// <summary>When it was sealed, for the message that explains why the password stopped working.</summary>
    public string? SealedAt { get; set; }

    /// <summary>Why it was sealed, in the words of whatever noticed. Shown to the user.</summary>
    public string? SealedReason { get; set; }

    public static VaultKeys Create(
        string userPassword, string guardianCode, byte[] machineSecret,
        int iterations = VaultCrypto.DefaultIterations) =>
        ReWrap(VaultCrypto.NewKey(), userPassword, guardianCode, machineSecret, iterations);

    /// <summary>
    /// Puts new wrappings around an existing data key. Used both for a new vault and for coming back
    /// from a seal with the guardian code: the key is the same, so the files come back untouched,
    /// and only the two doors are rebuilt.
    /// </summary>
    public static VaultKeys ReWrap(
        byte[] key, string userPassword, string guardianCode, byte[] machineSecret,
        int iterations = VaultCrypto.DefaultIterations)
    {
        var normalized = VaultCrypto.NormalizeGuardianCode(guardianCode)
            ?? throw new ArgumentException("监护码格式不对。", nameof(guardianCode));

        var keys = new VaultKeys { Iterations = iterations, MachineBound = true };

        keys.UserSalt = VaultCrypto.NewSalt();
        keys.WrappedByUser = VaultCrypto.Wrap(
            key, VaultCrypto.DeriveBoundKey(userPassword, keys.UserSalt, iterations, machineSecret));

        keys.GuardianSalt = VaultCrypto.NewSalt();
        keys.WrappedByGuardian = VaultCrypto.Wrap(key, VaultCrypto.DeriveKey(normalized, keys.GuardianSalt, iterations));

        return keys;
    }

    /// <summary>
    /// Re-wraps the data key under a new user password, leaving the guardian's wrapping untouched.
    ///
    /// <para>
    /// Changing one door without knowing the other is the point. The guardian code is with somebody
    /// else; a password change that needed it would mean a trip to them every time, and the reason
    /// somebody changes their password is usually that they want to do it now.
    /// </para>
    /// <para>
    /// It deliberately does not touch <see cref="State"/>. A sealed vault stays sealed: giving it a
    /// user password again must not quietly put back the door that was taken away.
    /// </para>
    /// </summary>
    public void SetUserPassword(byte[] key, string password, byte[] machineSecret)
    {
        UserSalt = VaultCrypto.NewSalt();
        WrappedByUser = VaultCrypto.Wrap(
            key, VaultCrypto.DeriveBoundKey(password, UserSalt, Iterations, machineSecret));

        MachineBound = true;
    }

    /// <summary>Re-wraps the data key under a new guardian code, leaving the user's wrapping alone.</summary>
    public void SetGuardianCode(byte[] key, string code)
    {
        var normalized = VaultCrypto.NormalizeGuardianCode(code)
            ?? throw new ArgumentException("监护码格式不对。", nameof(code));

        GuardianSalt = VaultCrypto.NewSalt();
        WrappedByGuardian = VaultCrypto.Wrap(key, VaultCrypto.DeriveKey(normalized, GuardianSalt, Iterations));
    }

    /// <summary>
    /// 用密码开门。<paramref name="machineSecret"/> 是这台机器上那份秘密 ——
    /// 绑定的门**缺了它开不了**，那正是"旧备份换台机器就作废"的原理。
    ///
    /// <para>
    /// Null when the password is wrong — or when the vault has been sealed, which looks the same
    /// from here. 老文件（<see cref="MachineBound"/> 为 false）不需要那份秘密。
    /// </para>
    /// </summary>
    public byte[]? UnlockWithUserPassword(string password, byte[]? machineSecret = null)
    {
        if (State != VaultState.Normal || UserSalt is null || WrappedByUser is null)
            return null;

        if (MachineBound && machineSecret is null)
            return null;   // 绑了机器却没拿到那份秘密：开不了就别装作开得了

        var derived = MachineBound
            ? VaultCrypto.DeriveBoundKey(password, UserSalt, Iterations, machineSecret!)
            : VaultCrypto.DeriveKey(password, UserSalt, Iterations);

        return VaultCrypto.TryUnwrap(WrappedByUser, derived, out var key) ? key : null;
    }

    /// <summary>
    /// 就地升级：把"只用密码"的老门换成绑定机器的新门。内容一个字节都不动，只换包装。
    ///
    /// <para>
    /// 只在**已经用密码成功打开**之后调用 —— 那时候手上就有那把钥匙，换包装是安全的。
    /// </para>
    /// </summary>
    public void BindToMachine(byte[] key, string password, byte[] machineSecret)
    {
        if (MachineBound)
            return;

        SetUserPassword(key, password, machineSecret);
    }

    public byte[]? UnlockWithGuardianCode(string code)
    {
        var normalized = VaultCrypto.NormalizeGuardianCode(code);
        if (normalized is null || GuardianSalt is null || WrappedByGuardian is null)
            return null;

        var derived = VaultCrypto.DeriveKey(normalized, GuardianSalt, Iterations);
        return VaultCrypto.TryUnwrap(WrappedByGuardian, derived, out var key) ? key : null;
    }

    /// <summary>
    /// Takes the user's door off. Idempotent: being told twice that the protection is gone is the
    /// normal case, not an error.
    ///
    /// <para>
    /// Note what is <em>not</em> touched: the wrapped data key, the files, the guardian's wrapping.
    /// Only the user's own wrapping is discarded, and that discard is what cannot be undone without
    /// the guardian. Deleting more would be destroying data to make a point.
    /// </para>
    /// </summary>
    public bool SealToGuardian(string reason, DateTimeOffset at)
    {
        if (State == VaultState.SealedToGuardian)
            return false;

        WrappedByUser = null;
        UserSalt = null;
        State = VaultState.SealedToGuardian;
        SealedReason = reason;
        SealedAt = at.ToString("yyyy-MM-dd HH:mm:ss");
        return true;
    }

    /// <summary>True when the user's password is expected to work. Not part of the file.</summary>
    [JsonIgnore]
    public bool UserDoorAvailable => State == VaultState.Normal && WrappedByUser is not null;

    /// <summary>
    /// 这份钥匙的字节，但**不带"密码那道门"**。
    ///
    /// <para>
    /// 给备份用的。封印的原理是"把这道门从钥匙文件里删掉"，而备份是逐字节快照 ——
    /// 带上它，就等于额外造了一个随时能把封印撤销的副本（导回来、或者写个客户端直接读它，
    /// 密码又管用了）。备份要防的是"盘坏了/卸载了"，而恢复时用监护码就够了：
    /// 那正是监护码存在的意义。
    /// </para>
    /// <para>
    /// 顺带把状态标成"已封印到监护码"，理由也写上 —— 这样恢复出来那份一旦有人拿密码去开，
    /// 界面会说"只能拿监护码打开"，而不是含糊的"密码不对"。
    /// </para>
    /// </summary>
    public byte[] ToBytesWithoutUserDoor(DateTimeOffset when, string reason)
    {
        var copy = new VaultKeys
        {
            Version = Version,
            Iterations = Iterations,
            GuardianSalt = GuardianSalt,
            WrappedByGuardian = WrappedByGuardian,
            State = VaultState.SealedToGuardian,
            SealedAt = when.ToString("yyyy-MM-dd HH:mm:ss"),
            SealedReason = reason,

            // 这两项**故意留空** —— 就是"那道门"本身。
            UserSalt = null,
            WrappedByUser = null,
        };

        // 空的项干脆不写出来：读那份 JSON 的人（包括将来排查问题的人）一眼就能看出
        // "这里没有密码门"，而不是看见一对 null 去猜它是什么意思。
        var options = new JsonSerializerOptions(Options)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        return System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(copy, options));
    }

    public static VaultKeys? Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<VaultKeys>(File.ReadAllText(path), Options) : null;
        }
        catch
        {
            return null;
        }
    }

    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
    }
}
