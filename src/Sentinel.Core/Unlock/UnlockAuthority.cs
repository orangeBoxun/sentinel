using System.Security.Cryptography;
using Sentinel.Core.Config;
using Sentinel.Core.Models;

namespace Sentinel.Core.Unlock;

public enum UnlockVerdict
{
    /// <summary>The edit does not weaken anything, so no token was ever going to be needed.</summary>
    NotRequired,

    Granted,

    Refused,
}

/// <summary>
/// Decides whether an edit is allowed to take effect.
///
/// <para>
/// This is the one place a relaxation can be stopped, and it is deliberately the only one: the
/// service owns the config file, so gating <c>TrySave</c> here covers the named pipe, the tray UI and
/// the file on disk at once. Every other config path leads back to this class.
/// </para>
/// <para>
/// <b>The public key is not the secret.</b> It sits next to the config, readable by anyone. What
/// makes this work is that the matching private key never touches this machine.
/// </para>
/// </summary>
public sealed class UnlockAuthority
{
    public const string PublicKeyFileName = "unlock-key.pub";

    /// <summary>How long a token is good for. Long enough to walk to the other device and back.</summary>
    public static readonly TimeSpan DefaultValidity = TimeSpan.FromHours(24);

    private readonly RSA _publicKey;

    private UnlockAuthority(RSA publicKey) => _publicKey = publicKey;

    public static string PublicKeyPathFor(string configPath)
    {
        var directory = Path.GetDirectoryName(configPath);
        return string.IsNullOrEmpty(directory)
            ? PublicKeyFileName
            : Path.Combine(directory, PublicKeyFileName);
    }

    /// <summary>
    /// Loads the public key, or returns null when unlock protection has not been set up.
    ///
    /// "Not set up" is a normal state, not an error: this is opt-in, and a machine without a key
    /// behaves exactly as it did before the feature existed. Refusing every save until a key
    /// appeared would make the install a trap.
    /// </summary>
    public static UnlockAuthority? TryLoad(string configPath, out string? reason)
    {
        reason = null;
        var path = PublicKeyPathFor(configPath);

        if (!File.Exists(path))
        {
            reason = $"未配置（没有 {path}）";
            return null;
        }

        try
        {
            var rsa = RSA.Create();
            rsa.ImportFromPem(File.ReadAllText(path));
            return new UnlockAuthority(rsa);
        }
        catch (Exception ex)
        {
            reason = $"公钥文件无法读取：{ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// Whether a key may be put into service right now.
    ///
    /// <para>
    /// Refused when one is already loaded. Without this, "enable the protection" would double as a way
    /// to swap in a key whose private half you hold — the whole mechanism undone in one step. Replacing
    /// means releasing first, and releasing needs a token.
    /// </para>
    /// <para>
    /// The rule lives here rather than in the Service that enforces it so that it can be tested as a
    /// rule: it is the one thing standing between "turn protection on" and "turn protection into
    /// decoration".
    /// </para>
    /// </summary>
    public static bool MayEnable(bool alreadyConfigured, out string? error)
    {
        error = alreadyConfigured
            ? "解锁保护已经开着。想换一把钥匙，先用令牌关闭保护，再重新开启。"
            : null;

        return !alreadyConfigured;
    }

    /// <summary>
    /// Checks that a pasted blob really is an RSA public key, and hands back the one spelling this
    /// program stores.
    ///
    /// <para>
    /// Validating at the moment of pasting matters more than it looks. A key that gets written but
    /// cannot be verified turns "I can relax this when I mean to" into "I can never relax this
    /// again" — and that only shows up the first time the user actually needs a token, which is the
    /// worst possible moment to find out.
    /// </para>
    /// </summary>
    public static bool TryPreparePublicKey(string? pasted, out string pem, out string? error)
    {
        pem = "";
        error = null;

        var text = (pasted ?? "").Trim();
        if (text.Length == 0)
        {
            error = "还没有粘公钥。";
            return false;
        }

        // Refused rather than quietly reduced to its public half: if the private key is in the box,
        // it is already on this machine, and the honest thing is to say so instead of storing a
        // usable public key and letting them believe nothing went wrong.
        if (text.Contains("PRIVATE KEY", StringComparison.Ordinal))
        {
            error = "这是私钥，不是公钥。私钥不该出现在这台机器上 —— "
                  + "它应该只留在手机里，这里粘的是页面给的那份 PUBLIC KEY。";
            return false;
        }

        if (!text.Contains("-----BEGIN", StringComparison.Ordinal))
        {
            error = "这看起来不是 PEM 格式 —— 应当以 -----BEGIN PUBLIC KEY----- 开头。";
            return false;
        }

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(text);

            if (rsa.KeySize < 2048)
            {
                error = $"这把钥匙只有 {rsa.KeySize} 位，太短了（至少 2048）。回页面上重新生成一把。";
                return false;
            }

            // Normalised on the way in: PKCS#1 and PKCS#8 spellings both import, but storing one
            // shape means the file on disk is always the same thing to look at.
            pem = rsa.ExportSubjectPublicKeyInfoPem();
            return true;
        }
        catch (Exception ex)
        {
            error = $"这不是一把能读出来的 RSA 公钥：{ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Builds the string you carry to the other machine. It names the exact configuration being
    /// asked for, so signing it is an informed act rather than a blank cheque.
    /// </summary>
    public string CreateRequest(AppConfig config, TimeSpan? validity = null) =>
        CreateRequestFor(UnlockCrypto.Digest(config), validity);

    /// <summary>
    /// The same machinery for the one thing that is not an edit: authorizing the end of the rule
    /// itself. Nothing about the token changes — it still covers a digest, it is still signed
    /// off-machine — only what the digest stands for.
    /// </summary>
    public string CreateRequestFor(string digest, TimeSpan? validity = null)
    {
        var expires = DateTimeOffset.UtcNow.Add(validity ?? DefaultValidity);
        var payload = UnlockCrypto.CanonicalPayload(digest, expires.ToString("O"), UnlockCrypto.NewNonce());

        return UnlockCrypto.EncodeRequest(payload);
    }

    public UnlockVerdict Authorize(AppConfig previous, AppConfig current, string? tokenText, out string? reason)
    {
        reason = null;

        if (ConfigRelaxation.Classify(previous, current) != ChangeDirection.Relaxing)
            return UnlockVerdict.NotRequired;

        return Validate(current, tokenText, out reason) ? UnlockVerdict.Granted : UnlockVerdict.Refused;
    }

    /// <summary>
    /// The same decision, also reporting which token was accepted so the caller can spend it. A token
    /// that is not recorded as spent can be presented again the next time the same change is wanted,
    /// which turns one trip to the key into a permanent allowance.
    /// </summary>
    public UnlockVerdict Authorize(
        AppConfig previous, AppConfig current, string? tokenText, out string? reason, out string? nonce)
    {
        nonce = null;

        if (ConfigRelaxation.Classify(previous, current) != ChangeDirection.Relaxing)
        {
            reason = null;
            return UnlockVerdict.NotRequired;
        }

        return ValidateDigest(UnlockCrypto.Digest(current), tokenText, out reason, out nonce)
            ? UnlockVerdict.Granted
            : UnlockVerdict.Refused;
    }

    /// <summary>
    /// Checks a token against the config it claims to authorize, without needing to know what the
    /// config used to be. Both entry points need this shape: a save has a previous config to compare
    /// against, but the file on disk at startup does not.
    /// </summary>
    public bool Validate(AppConfig config, string? tokenText, out string? reason) =>
        ValidateDigest(UnlockCrypto.Digest(config), tokenText, out reason);

    /// <summary>
    /// The check itself, against any digest. <paramref name="what"/> names the thing being refused,
    /// because "this edit weakens things" and "this would end the rule" are different sentences and
    /// the user is owed the right one.
    /// </summary>
    public bool ValidateDigest(
        string expected, string? tokenText, out string? reason, string what = "这次修改会放宽限制") =>
        ValidateDigest(expected, tokenText, out reason, out _, what);

    public bool ValidateDigest(
        string expected, string? tokenText, out string? reason, out string? nonce,
        string what = "这次修改会放宽限制")
    {
        reason = null;
        nonce = null;

        if (string.IsNullOrWhiteSpace(tokenText))
        {
            reason = $"{what}，需要解锁令牌。";
            return false;
        }

        if (!UnlockCrypto.TryDecodeToken(tokenText, out var payload, out var signature))
        {
            reason = "令牌格式无法识别（应当以 TOK1. 开头）。";
            return false;
        }

        if (!UnlockCrypto.Verify(_publicKey, payload, signature))
        {
            // Covers a wrong key, a truncated paste and a hand-edited payload alike. Which one it was
            // is not something the caller can act on, so it is one message.
            reason = "令牌签名校验失败。";
            return false;
        }

        if (!UnlockCrypto.TryReadPayload(payload, out var digest, out var expires, out _))
        {
            reason = "令牌内容无法解析。";
            return false;
        }

        if (!string.Equals(digest, expected, StringComparison.Ordinal))
        {
            // The ordinary cause is editing the config after asking for the token. Say so: the fix is
            // to request a fresh one, and that is not obvious from "digest mismatch".
            reason = "令牌对应的是另一次操作 —— 签发之后目标又改动过。请重新获取解锁码。";
            return false;
        }

        if (DateTimeOffset.UtcNow > expires)
        {
            reason = $"令牌已于 {expires.ToLocalTime():yyyy-MM-dd HH:mm} 过期。";
            return false;
        }

        nonce = ReadNonce(payload);
        return true;
    }

    private static string? ReadNonce(string payload) =>
        UnlockCrypto.TryReadPayload(payload, out _, out _, out var nonce) ? nonce : null;
}
