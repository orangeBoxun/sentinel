using System.Security.Cryptography;
using System.Text;

namespace Sentinel.Core.Vault;

/// <summary>
/// The primitives the vault is built from: deriving a key from something a person knows, wrapping
/// the vault's data key with it, and the one string that is meant to be written down by hand.
///
/// <para>
/// Everything here is authenticated. A wrong password must produce "no", never a plausible-looking
/// key that then decrypts the vault into garbage — the difference matters when the thing behind it
/// is the only copy of a file.
/// </para>
/// </summary>
public static class VaultCrypto
{
    public const int KeySize = 32;
    public const int TagSize = 16;
    private const int NonceSize = 12;
    private const int SaltSize = 16;

    /// <summary>
    /// Cost of turning the user's password into a key. The password is the weak part — the user
    /// chose it — so this is set to make guessing it expensive rather than to make the file
    /// unreadable, which AES already handles.
    /// </summary>
    public const int DefaultIterations = 600_000;

    public static byte[] DeriveKey(string secret, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(secret, salt, iterations, HashAlgorithmName.SHA256, KeySize);

    /// <summary>
    /// 用户那道门的包装钥匙：**密码 + 这台机器上的一份秘密**，两样都要。
    ///
    /// <para>
    /// 为什么不是只用密码：封印的原理是"把用户那道门从钥匙文件里删掉"，但**旧钥匙文件的副本里
    /// 那道门还在** —— 谁在封印之前把 vault 目录拷一份出去（或者点一下界面的导出），谁就能把
    /// 封印撤掉。给那道门再绑一份"只存在于这台机器、而封印/卸载时会销毁"的秘密之后，
    /// 那份旧副本就不够用了：它还缺这份秘密。
    /// </para>
    /// <para>
    /// 监护码那道门**不绑机器** —— 它存在的意义恰恰是"机器没了、重装了、被封了，还能回来"。
    /// </para>
    /// </summary>
    public static byte[] DeriveBoundKey(string password, byte[] salt, int iterations, byte[] machineSecret) =>
        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, KeySize),
            KeySize,
            machineSecret,
            Encoding.UTF8.GetBytes("sentinel-vault-user-door"));

    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(KeySize);

    public static byte[] NewSalt() => RandomNumberGenerator.GetBytes(SaltSize);

    // ------------------------------------------------------------------ 封装

    /// <summary>
    /// Wraps the data key with a key derived from a password. AES-GCM, so the tag makes a wrong
    /// password detectable rather than merely wrong.
    /// </summary>
    public static byte[] Wrap(byte[] key, byte[] wrappingKey)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[key.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(wrappingKey, TagSize);
        aes.Encrypt(nonce, key, cipher, tag);

        return [.. nonce, .. tag, .. cipher];
    }

    public static bool TryUnwrap(byte[] blob, byte[] wrappingKey, out byte[] key)
    {
        key = [];

        if (blob.Length != NonceSize + TagSize + KeySize)
            return false;

        try
        {
            var plain = new byte[KeySize];

            using var aes = new AesGcm(wrappingKey, TagSize);
            aes.Decrypt(blob.AsSpan(0, NonceSize), blob.AsSpan(NonceSize + TagSize), blob.AsSpan(NonceSize, TagSize), plain);

            key = plain;
            return true;
        }
        catch (CryptographicException)
        {
            // Wrong password, or the blob was edited. Both are "no".
            key = [];
            return false;
        }
    }

    // ------------------------------------------------------------------ 监护码

    /// <summary>
    /// Crockford Base32: no I, L, O or U. The exclusions are not cosmetic — this alphabet exists so
    /// that a string copied onto paper and read back weeks later survives 1/I/l and 0/O.
    /// </summary>
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    private const int CodeBytes = 20;        // 160 bits — infeasible to guess, short enough to copy
    private const int CheckChars = 2;

    /// <summary>
    /// A new guardian code, formatted in groups for hand-copying.
    ///
    /// This is the string that goes to someone else. It is generated here rather than chosen by the
    /// user because a chosen password would be guessable by the one person who must not guess it.
    /// </summary>
    public static string NewGuardianCode()
    {
        var payload = Encode(RandomNumberGenerator.GetBytes(CodeBytes));
        return Group(payload + Checksum(payload));
    }

    /// <summary>
    /// Accepts what someone actually types: lowercase, dashes, spaces, and the confusable letters.
    /// Returns null when it is not a valid code — the checksum is what turns "wrong code" into
    /// "you mistyped it", which is the failure this format has to make visible.
    /// </summary>
    public static string? NormalizeGuardianCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return null;

        var compact = new StringBuilder(CodeBytes * 8 / 5 + CheckChars);

        foreach (var raw in code)
        {
            var c = char.ToUpperInvariant(raw);

            // Crockford's decoding rules: the letters that look like digits are the digits.
            c = c switch
            {
                'I' or 'L' => '1',
                'O' => '0',
                _ => c,
            };

            if (Alphabet.Contains(c))
                compact.Append(c);
        }

        var value = compact.ToString();
        if (value.Length != CodeBytes * 8 / 5 + CheckChars)
            return null;

        var payload = value[..^CheckChars];
        return Checksum(payload) == value[^CheckChars..] ? payload : null;
    }

    private static string Group(string compact)
    {
        var parts = new List<string>();
        for (var i = 0; i < compact.Length; i += 4)
            parts.Add(compact.Substring(i, Math.Min(4, compact.Length - i)));

        return string.Join('-', parts);
    }

    private static string Encode(byte[] bytes)
    {
        var text = new StringBuilder((bytes.Length * 8 + 4) / 5);
        var buffer = 0;
        var bits = 0;

        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bits += 8;

            while (bits >= 5)
            {
                bits -= 5;
                text.Append(Alphabet[(buffer >> bits) & 31]);
            }
        }

        if (bits > 0)
            text.Append(Alphabet[(buffer << (5 - bits)) & 31]);

        return text.ToString();
    }

    /// <summary>
    /// Two symbols derived from the payload. Not a security measure — it is there so a copy that
    /// went wrong on the way to paper is caught now rather than on the day it is needed.
    /// </summary>
    private static string Checksum(string payload)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(payload));
        return $"{Alphabet[hash[0] & 31]}{Alphabet[hash[1] & 31]}";
    }
}
