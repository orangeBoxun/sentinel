using System.Security.Cryptography;
using Sentinel.Core.Unlock;
using Xunit;

namespace Sentinel.Core.Tests;

/// <summary>
/// 手机页面生成的钥匙，拿回这台机器必须能用。
///
/// <para>
/// 这是整条链路上唯一一处"两边各自实现、靠约定对齐"的地方：页面用 WebCrypto 生成密钥对、用
/// WebCrypto 签名，这台机器用 .NET 读和验。任何一边换了参数 —— 密钥长度、导出的 DER 封装、
/// PSS 的盐长度 —— 都会在这里炸，而不是等到用户真需要解锁的那天才发现。
/// </para>
/// <para>
/// 下面这对钥匙和这个签名，是 <c>tools/unlock-page/sentinel-unlock.html</c> 里那段 JS
/// 用 node 的 WebCrypto 现跑出来的，参数和页面完全一致。
/// </para>
/// </summary>
public class UnlockInteropTests
{
    /// <summary>WebCrypto <c>exportKey("spki", …)</c> 的结果转成 PEM。</summary>
    private const string WebCryptoPublicKey = """
        -----BEGIN PUBLIC KEY-----
        MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEA5YukVx27eYYbaCqwnQ9V
        55PCsPG7uSFCtPViEXuP/QVu4r1ktnx18pjtgT8sPtsVz3XymSgZg2D1frr8j+Ud
        UovtzaYqPsHJy0BQF2r3bvinIYom6NwNt5VOPlxWNJch2jiku0/85SiVGRiRJfb6
        wuJ8lOBxK/ktOi53OoBffMs5s0kKpY7WYpN0pJafEpQcgPLcE945mscJwZ3b1mVm
        jSDjDleyXngxj7ApbZabuQh0wSy/WsNNqU2dLOF/DTcQj7JcZ+61ytivuxSzoqg4
        3C41Jb2gMstp61wF+TdEjwVhr11DvSPU4/vYdcXMJL/H6MBZ7KpaNc32LanS5j2P
        1QIDAQAB
        -----END PUBLIC KEY-----
        """;

    /// <summary>WebCrypto <c>sign({name:"RSA-PSS", saltLength:32}, …)</c> 的原始字节。</summary>
    private const string SignatureBase64 =
        "sotaQfx6w3UbRKaTbMRPOglHYniSZOfLKMcwxmunctngFuMMaBlWnCL/Ujv8haK0UfJg7pURRwi/aP3P6KdRqi29B8vNs3W8Z/gH8znBaGPMctBx+g9kb+yKJpzt8x3EoXbN8Mpr8gknim9sRuphsTC30JYip7TFKxPv3ExUpXKd1tBCtHVedkWJl7ZWRC/1pcxtVn7TuWsha2E4vc/tXXSWnqUuqJQChQGNqB4QJHuUwE6zFdykvbBdWLQYbETJrBu6PfXp7P+gdVNgqAS0fzMoDrA3fiisRFk9EkL0a70LdA6jf8e6jmOEv4HpptEWJuLvC3ohOusSZR2JH/dtTg==";

    private const string Payload = "sentinel-interop-check";

    [Fact]
    public void A_key_generated_by_the_phone_page_is_accepted()
    {
        Assert.True(UnlockAuthority.TryPreparePublicKey(WebCryptoPublicKey, out var pem, out var error), error);
        Assert.Contains("BEGIN PUBLIC KEY", pem);
    }

    [Fact]
    public void A_signature_made_by_the_phone_page_verifies_here()
    {
        // 盐长度对不上，或者 DER 封装差一层，都会让这里返回 false —— 而线上表现是
        // "令牌签名校验失败"，用户完全无从判断是抄错了还是程序不兼容。
        Assert.True(UnlockAuthority.TryPreparePublicKey(WebCryptoPublicKey, out var pem, out _));

        using var rsa = RSA.Create();
        rsa.ImportFromPem(pem);

        Assert.True(UnlockCrypto.Verify(rsa, Payload, Convert.FromBase64String(SignatureBase64)));
    }

    [Fact]
    public void Pasting_the_private_key_is_refused()
    {
        // 私钥到了这台机器上，整套机制就没了意义。与其静默地只取它的公钥那一半、
        // 让人以为一切正常，不如直接说不。
        var privateLabeled = WebCryptoPublicKey.Replace("PUBLIC KEY", "PRIVATE KEY");

        Assert.False(UnlockAuthority.TryPreparePublicKey(privateLabeled, out _, out var error));
        Assert.Contains("私钥", error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("hello")]
    [InlineData("-----BEGIN PUBLIC KEY-----\n这不是 base64\n-----END PUBLIC KEY-----")]
    public void Anything_that_is_not_a_public_key_is_refused(string text)
    {
        Assert.False(UnlockAuthority.TryPreparePublicKey(text, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void A_key_too_short_to_be_worth_anything_is_refused()
    {
        using var small = RSA.Create(1024);

        Assert.False(UnlockAuthority.TryPreparePublicKey(small.ExportSubjectPublicKeyInfoPem(), out _, out var error));
        Assert.Contains("2048", error);
    }

    [Fact]
    public void Turning_protection_on_is_allowed_only_while_it_is_off()
    {
        Assert.True(UnlockAuthority.MayEnable(alreadyConfigured: false, out var noError));
        Assert.Null(noError);

        // 已经开着就不许从这扇门换钥匙。否则"开启保护"同时就是"换成一把我握有私钥的钥匙" ——
        // 整套机制一步就没了，而且看起来像是正常操作。要换，先用令牌关掉。
        Assert.False(UnlockAuthority.MayEnable(alreadyConfigured: true, out var error));
        Assert.Contains("令牌", error);
    }

    [Fact]
    public void Whatever_spelling_was_pasted_one_shape_is_what_gets_stored()
    {
        // PKCS#1（BEGIN RSA PUBLIC KEY）和 PKCS#8（BEGIN PUBLIC KEY）都能导入，
        // 但写进磁盘的永远是同一种 —— 那个文件始终是同一个样子。
        using var rsa = RSA.Create(2048);

        Assert.True(UnlockAuthority.TryPreparePublicKey(rsa.ExportRSAPublicKeyPem(), out var fromPkcs1, out _));
        Assert.True(UnlockAuthority.TryPreparePublicKey(rsa.ExportSubjectPublicKeyInfoPem(), out var fromPkcs8, out _));

        Assert.Equal(fromPkcs8, fromPkcs1);
        Assert.Contains("BEGIN PUBLIC KEY", fromPkcs1);
    }
}
