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

    /// <summary>
    /// "用户那把派生钥匙"的一份副本，**用监护码加密**。
    ///
    /// <para>
    /// 存在的唯一理由：封印把用户那道门连包装一起删了，所以"下次还用原来那个密码"在正常情形下
    /// 是做不到的（信息没了）。但用监护码解除封印时，人已经在场 —— 于是可以事先把"派生钥匙"
    /// 寄存在监护码底下，那时取出它、配上新的本机秘密，就能把门原样包回来，用户**什么都不用填**。
    /// </para>
    /// <para>
    /// 它不比保险箱本身更弱：解开它需要监护码（160 位随机 + 60 万次 PBKDF2）。而封印仍然照旧
    /// 销毁本机秘密 —— 所以"封印之前拷出去的副本"还是打不开（那份副本里没有本机秘密）。
    /// </para>
    /// <para>
    /// 改密码时会把它清掉：否则旧密码会在解封之后复活 —— 那是安全倒退。
    /// </para>
    /// </summary>
    public byte[]? GuardianSealedUserKey { get; set; }

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

        var derived = VaultCrypto.DeriveKey(userPassword, keys.UserSalt, iterations);

        keys.WrappedByUser = VaultCrypto.Wrap(key, VaultCrypto.BindToMachine(derived, machineSecret));

        keys.GuardianSalt = VaultCrypto.NewSalt();

        var guardianKey = VaultCrypto.DeriveKey(normalized, keys.GuardianSalt, iterations);

        keys.WrappedByGuardian = VaultCrypto.Wrap(key, guardianKey);
        keys.GuardianSealedUserKey = VaultCrypto.Encrypt(UserKeyPayload(derived, keys.UserSalt), guardianKey);

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
    /// <param name="guardianCodeForStorage">
    /// 手上正好有监护码时传进来（比如"用监护码解开封印、顺手设个新密码"）—— 那就把新密码的
    /// 派生钥匙也寄存一份，下次封印之后照样免密码。没有它就只能作废那份寄存（否则旧密码会在
    /// 解封之后复活，那是安全倒退）。
    /// </param>
    public void SetUserPassword(byte[] key, string password, byte[] machineSecret, string? guardianCodeForStorage = null)
    {
        UserSalt = VaultCrypto.NewSalt();

        var derived = VaultCrypto.DeriveKey(password, UserSalt, Iterations);

        WrappedByUser = VaultCrypto.Wrap(key, VaultCrypto.BindToMachine(derived, machineSecret));

        MachineBound = true;
        GuardianSealedUserKey = null;

        var normalized = VaultCrypto.NormalizeGuardianCode(guardianCodeForStorage);

        if (normalized is not null && GuardianSalt is not null)
        {
            var guardianKey = VaultCrypto.DeriveKey(normalized, GuardianSalt, Iterations);

            GuardianSealedUserKey = VaultCrypto.Encrypt(UserKeyPayload(derived, UserSalt), guardianKey);
        }
    }

    /// <summary>
    /// 用监护码把"原来的用户钥匙"取回来，然后配上新的本机秘密，把门原样包回去。
    ///
    /// <para>
    /// 这是"用监护码打开之后，原来的密码A继续管用、什么都不用填"那条路。失败返回 false
    /// （没寄存过、监护码不对、或者数据坏了）—— 调用方那时就该退回"请设一个新密码"。
    /// </para>
    /// </summary>
    public bool TryRestoreUserDoor(byte[] key, string guardianCode, byte[] machineSecret)
    {
        if (GuardianSealedUserKey is null || GuardianSalt is null)
            return false;

        var normalized = VaultCrypto.NormalizeGuardianCode(guardianCode);

        if (normalized is null)
            return false;

        var guardianKey = VaultCrypto.DeriveKey(normalized, GuardianSalt, Iterations);

        if (!VaultCrypto.TryDecrypt(GuardianSealedUserKey, guardianKey, out var payload)
            || payload.Length != VaultCrypto.KeySize + 16)
        {
            return false;
        }

        var derived = payload[..VaultCrypto.KeySize];
        var salt = payload[VaultCrypto.KeySize..];

        UserSalt = salt;
        WrappedByUser = VaultCrypto.Wrap(key, VaultCrypto.BindToMachine(derived, machineSecret));
        MachineBound = true;

        return true;
    }

    /// <summary>寄存进监护码底下的那份东西：派生钥匙 + 它当初用的盐。盐要还回去，同一个密码才认。</summary>
    private static byte[] UserKeyPayload(byte[] derived, byte[] salt) => [.. derived, .. salt];

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

    /// <summary>
    /// 把状态收回"正常"：监护码进来之后解除封印用。
    ///
    /// <para>
    /// 它本身不设任何门 —— 调用方必须**已经**用监护码打开了保险箱（<see cref="UnlockWithGuardianCode"/>
    /// 是唯一的入口），否则这就是一个"把封印擦掉"的后门。
    /// </para>
    /// </summary>
    public void ResetToNormal()
    {
        State = VaultState.Normal;
        SealedAt = null;
        SealedReason = null;
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
