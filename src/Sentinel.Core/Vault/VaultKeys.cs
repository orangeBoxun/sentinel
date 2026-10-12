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

    /// <summary>When it was sealed, for the message that explains why the password stopped working.</summary>
    public string? SealedAt { get; set; }

    /// <summary>Why it was sealed, in the words of whatever noticed. Shown to the user.</summary>
    public string? SealedReason { get; set; }

    public static VaultKeys Create(string userPassword, string guardianCode, int iterations = VaultCrypto.DefaultIterations) =>
        ReWrap(VaultCrypto.NewKey(), userPassword, guardianCode, iterations);

    /// <summary>
    /// Puts new wrappings around an existing data key. Used both for a new vault and for coming back
    /// from a seal with the guardian code: the key is the same, so the files come back untouched,
    /// and only the two doors are rebuilt.
    /// </summary>
    public static VaultKeys ReWrap(byte[] key, string userPassword, string guardianCode, int iterations = VaultCrypto.DefaultIterations)
    {
        var normalized = VaultCrypto.NormalizeGuardianCode(guardianCode)
            ?? throw new ArgumentException("监护码格式不对。", nameof(guardianCode));

        var keys = new VaultKeys { Iterations = iterations };

        keys.UserSalt = VaultCrypto.NewSalt();
        keys.WrappedByUser = VaultCrypto.Wrap(key, VaultCrypto.DeriveKey(userPassword, keys.UserSalt, iterations));

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
    public void SetUserPassword(byte[] key, string password)
    {
        UserSalt = VaultCrypto.NewSalt();
        WrappedByUser = VaultCrypto.Wrap(key, VaultCrypto.DeriveKey(password, UserSalt, Iterations));
    }

    /// <summary>Re-wraps the data key under a new guardian code, leaving the user's wrapping alone.</summary>
    public void SetGuardianCode(byte[] key, string code)
    {
        var normalized = VaultCrypto.NormalizeGuardianCode(code)
            ?? throw new ArgumentException("监护码格式不对。", nameof(code));

        GuardianSalt = VaultCrypto.NewSalt();
        WrappedByGuardian = VaultCrypto.Wrap(key, VaultCrypto.DeriveKey(normalized, GuardianSalt, Iterations));
    }

    /// <summary>Null when the password is wrong — or when the vault has been sealed, which looks the same from here.</summary>
    public byte[]? UnlockWithUserPassword(string password)
    {
        if (State != VaultState.Normal || UserSalt is null || WrappedByUser is null)
            return null;

        var derived = VaultCrypto.DeriveKey(password, UserSalt, Iterations);
        return VaultCrypto.TryUnwrap(WrappedByUser, derived, out var key) ? key : null;
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

    /// <summary>True when the user's password is expected to work.</summary>
    public bool UserDoorAvailable => State == VaultState.Normal && WrappedByUser is not null;

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
