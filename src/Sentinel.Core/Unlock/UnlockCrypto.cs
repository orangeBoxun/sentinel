using System.Security.Cryptography;
using System.Text;
using Sentinel.Core.Config;
using Sentinel.Core.Models;

namespace Sentinel.Core.Unlock;

/// <summary>
/// The unlock token: proof, made somewhere else, that a relaxation was authorized.
///
/// <para>
/// The shape is deliberately boring — <c>TOK1.&lt;payload&gt;.&lt;signature&gt;</c>, both base64url,
/// the payload being newline-separated text. Same reason a JWT is shaped that way: the thing that
/// has to produce these lives on a phone or another computer, and a format that is five lines to
/// reimplement in any language is a format that will still work in five years.
/// </para>
/// <para>
/// <b>The signature covers a digest of the config, not a permission.</b> A token says "this exact
/// configuration was authorized", never "relaxing is allowed now". That is what makes it safe to
/// leave lying around in a clipboard history: it unlocks one specific config and nothing else, and
/// once that config is in place it is spent — applying it again classifies as no change at all.
/// </para>
/// </summary>
public static class UnlockCrypto
{
    public const string PayloadPrefix = "sentinel-unlock";
    public const string PayloadVersion = "1";

    /// <summary>Marks a string as an unlock request — what the machine produces for you to carry out.</summary>
    public const string RequestPrefix = "REQ1";

    /// <summary>Marks a string as a signed token — what you bring back.</summary>
    public const string TokenPrefix = "TOK1";

    /// <summary>
    /// What a release token is signed over.
    ///
    /// <para>
    /// Every other token is signed over the digest of a configuration, because what is being
    /// authorized is one specific edit. This one authorizes the end of the rule itself, so there is
    /// no configuration to name — a fixed word stands in for one, and the resulting digest is
    /// covered by exactly the same machinery.
    /// </para>
    /// </summary>
    public const string ReleaseSubject = "sentinel-release-unlock-protection";

    public static readonly string ReleaseDigest = DigestOf(ReleaseSubject);

    private static string DigestOf(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>
    /// Text, not bytes, so it can go in a message or be read aloud. The digest is over the whole
    /// config, including fields that have nothing to do with enforcement, which is fine and on
    /// purpose: it means the token cannot be transplanted onto a different edit at all.
    /// </summary>
    public static string Digest(AppConfig config) => DigestOfConfig(ConfigStore.CanonicalBytes(config));

    private static string DigestOfConfig(byte[] canonical) =>
        Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();

    public static string CanonicalPayload(string digest, string expiresAt, string nonce) =>
        string.Join('\n', PayloadPrefix, PayloadVersion, digest, expiresAt, nonce);

    public static string NewNonce() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();

    // ------------------------------------------------------------------ 请求

    /// <summary>Wraps a canonical payload so it survives being pasted through a chat app or a phone.</summary>
    public static string EncodeRequest(string payload) =>
        $"{RequestPrefix}.{Base64Url(Encoding.UTF8.GetBytes(payload))}";

    public static bool TryDecodeRequest(string? text, out string payload)
    {
        payload = "";
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var parts = text.Trim().Split('.');
        if (parts.Length != 2 || parts[0] != RequestPrefix)
            return false;

        return TryBase64Url(parts[1], out payload);
    }

    // ------------------------------------------------------------------ 令牌

    public static string EncodeToken(string payload, byte[] signature) =>
        $"{TokenPrefix}.{Base64Url(Encoding.UTF8.GetBytes(payload))}.{Base64Url(signature)}";

    public static bool TryDecodeToken(string? text, out string payload, out byte[] signature)
    {
        payload = "";
        signature = [];

        if (string.IsNullOrWhiteSpace(text))
            return false;

        var parts = text.Trim().Split('.');
        if (parts.Length != 3 || parts[0] != TokenPrefix)
            return false;

        return TryBase64Url(parts[1], out payload) && TryBase64UrlBytes(parts[2], out signature);
    }

    // ------------------------------------------------------------------ 载荷

    public static bool TryReadPayload(string? payload, out string digest, out DateTimeOffset expires, out string nonce)
    {
        digest = "";
        nonce = "";
        expires = default;

        if (string.IsNullOrWhiteSpace(payload))
            return false;

        var parts = payload.Split('\n');
        if (parts.Length != 5 || parts[0] != PayloadPrefix || parts[1] != PayloadVersion)
            return false;

        if (!DateTimeOffset.TryParse(parts[3], null, System.Globalization.DateTimeStyles.RoundtripKind, out expires))
            return false;

        digest = parts[2];
        nonce = parts[4];
        return digest.Length == 64;
    }

    // ------------------------------------------------------------------ 签名

    public static byte[] Sign(RSA privateKey, string payload) =>
        privateKey.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);

    public static bool Verify(RSA publicKey, string payload, byte[] signature)
    {
        try
        {
            return publicKey.VerifyData(
                Encoding.UTF8.GetBytes(payload), signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        }
        catch (CryptographicException)
        {
            // A malformed signature is a failed verification, not a crash — this runs on input that
            // anyone who can reach the pipe gets to supply.
            return false;
        }
    }

    // ------------------------------------------------------------------ 编码

    /// <summary>base64url: '+/' and '=' are exactly the characters that get mangled in transit.</summary>
    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryBase64Url(string text, out string utf8)
    {
        utf8 = "";
        if (!TryBase64UrlBytes(text, out var bytes))
            return false;

        utf8 = Encoding.UTF8.GetString(bytes);
        return true;
    }

    private static bool TryBase64UrlBytes(string text, out byte[] bytes)
    {
        bytes = [];

        try
        {
            var padded = text.Replace('-', '+').Replace('_', '/');
            padded = (padded.Length % 4) switch
            {
                2 => padded + "==",
                3 => padded + "=",
                0 => padded,
                _ => padded + "___",   // invalid length; fails the decode below
            };

            bytes = Convert.FromBase64String(padded);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
