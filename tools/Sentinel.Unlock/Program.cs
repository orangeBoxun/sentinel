using System.Security.Cryptography;
using System.Text;
using Sentinel.Core.Unlock;

namespace Sentinel.Unlock;

/// <summary>
/// The half of unlock protection that stays away from the machine being controlled.
///
/// <para>
/// This is a separate tool on purpose. It could have been another switch on Sentinel.Service.exe,
/// and that would have been a mistake: the private key would then live on the same disk as the
/// thing it is supposed to constrain, and having it there means any scheme built on it is theatre.
/// Keeping it here means the signing key can sit on a USB stick, an old laptop, or a friend's
/// computer — wherever it is that you cannot casually reach.
/// </para>
/// <para>
/// It targets plain net8.0 so it can be published for any platform. What it needs from the machine
/// is nothing: a request string in, a token string out.
/// </para>
/// </summary>
internal static class Program
{
    private const int KeySizeBits = 3072;
    private const string PrivateKeyFile = "unlock-private.pem";
    private const string PublicKeyFile = "unlock-key.pub";

    private static int Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch
        {
            // Redirected output on some hosts rejects the encoder change. The strings we print are
            // ASCII in the parts that matter, so this is not worth failing over.
        }

        var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
        var keyPath = Option(args, "--key") ?? PrivateKeyFile;

        return command switch
        {
            "keygen" => Keygen(args),
            "show" => Show(args),
            "sign" => Sign(args, keyPath),
            "verify" => Verify(args, keyPath),
            "help" or "--help" or "-h" => Help(),
            _ => Unknown(command),
        };
    }

    // ------------------------------------------------------------------ keygen

    private static int Keygen(string[] args)
    {
        var directory = Option(args, "--out") ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(directory);

        var privatePath = Path.Combine(directory, PrivateKeyFile);
        var publicPath = Path.Combine(directory, PublicKeyFile);

        if (File.Exists(privatePath))
        {
            Console.Error.WriteLine($"已存在私钥，没有覆盖：{privatePath}");
            Console.Error.WriteLine("覆盖它会让之前发出的令牌全部失效。确认要重做请先手动删掉。");
            return 1;
        }

        using var rsa = RSA.Create(KeySizeBits);
        File.WriteAllText(privatePath, rsa.ExportRSAPrivateKeyPem());
        File.WriteAllText(publicPath, rsa.ExportSubjectPublicKeyInfoPem());

        Console.WriteLine("已生成密钥对。");
        Console.WriteLine();
        Console.WriteLine($"  私钥（不要带进被管控的机器）: {privatePath}");
        Console.WriteLine($"  公钥（拷到被管控的机器的配置目录）: {publicPath}");
        Console.WriteLine();
        Console.WriteLine("把公钥放到 config.json 旁边，改名成 unlock-key.pub，解锁保护就生效了。");
        Console.WriteLine("公钥可以直接看，放着不管也没关系；私钥丢了就再也签不出解锁码，只能重新生成密钥对。");
        return 0;
    }

    // ------------------------------------------------------------------ show

    private static int Show(string[] args)
    {
        if (!TryReadRequest(args, out var payload))
            return 1;

        if (!UnlockCrypto.TryReadPayload(payload, out var digest, out var expires, out var nonce))
        {
            Console.Error.WriteLine("解锁码内容无法解析。");
            return 1;
        }

        Console.WriteLine("这份解锁码要授权的是：");
        Console.WriteLine($"  配置指纹 : {digest}");
        Console.WriteLine($"  有效期至 : {expires.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine($"  随机号   : {nonce}");
        Console.WriteLine();
        Console.WriteLine("签名前请确认这确实是你自己在被管控机器上做的修改。");
        return 0;
    }

    // ------------------------------------------------------------------ sign

    private static int Sign(string[] args, string keyPath)
    {
        if (!TryReadRequest(args, out var payload))
            return 1;

        if (!UnlockCrypto.TryReadPayload(payload, out _, out var expires, out _))
        {
            Console.Error.WriteLine("解锁码内容无法解析。");
            return 1;
        }

        if (DateTimeOffset.UtcNow > expires)
        {
            Console.Error.WriteLine($"这份解锁码在签之前就已经过期了（{expires.ToLocalTime():yyyy-MM-dd HH:mm}）。");
            Console.Error.WriteLine("请回到被管控的机器上重新获取一份。");
            return 1;
        }

        RSA privateKey;
        try
        {
            privateKey = RSA.Create();
            privateKey.ImportFromPem(File.ReadAllText(keyPath));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"读不了私钥 {keyPath}：{ex.Message}");
            Console.Error.WriteLine("用 --key <路径> 指定位置。");
            return 1;
        }

        using (privateKey)
        {
            var signature = UnlockCrypto.Sign(privateKey, payload);
            Console.WriteLine(UnlockCrypto.EncodeToken(payload, signature));
        }

        return 0;
    }

    // ------------------------------------------------------------------ verify

    private static int Verify(string[] args, string keyPath)
    {
        var token = args.Length > 1 ? args[1] : null;
        if (string.IsNullOrWhiteSpace(token))
        {
            Console.Error.WriteLine("用法：sentinel-unlock verify <令牌> [--key <公钥>]");
            return 1;
        }

        if (!UnlockCrypto.TryDecodeToken(token, out var payload, out var signature))
        {
            Console.Error.WriteLine("令牌格式无法识别。");
            return 1;
        }

        using var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(File.ReadAllText(keyPath));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"读不了公钥 {keyPath}：{ex.Message}");
            return 1;
        }

        var ok = UnlockCrypto.Verify(rsa, payload, signature);
        Console.WriteLine(ok ? "签名有效。" : "签名无效。");
        return ok ? 0 : 1;
    }

    // ------------------------------------------------------------------ 杂项

    /// <summary>
    /// Reads the request from the command line, or from stdin when it is not there.
    ///
    /// The stdin path is not decoration: these strings travel through phone screens, chat apps and
    /// paper, and re-typing one is exactly the kind of transcription error that would look like a
    /// broken tool.
    /// </summary>
    private static bool TryReadRequest(string[] args, out string payload)
    {
        payload = "";

        var text = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal)
            ? args[1]
            : Console.In.ReadToEnd();

        if (string.IsNullOrWhiteSpace(text))
        {
            Console.Error.WriteLine("没有拿到解锁码。用法：sentinel-unlock sign <解锁码>");
            return false;
        }

        if (!UnlockCrypto.TryDecodeRequest(text.Trim(), out payload))
        {
            Console.Error.WriteLine("解锁码格式无法识别（应当以 REQ1. 开头）。");
            return false;
        }

        return true;
    }

    private static string? Option(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }

        return null;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"未知命令：{command}");
        return Help();
    }

    private static int Help()
    {
        Console.WriteLine("""
            sentinel-unlock —— 在别的机器上签发解锁码。

              keygen [--out <目录>]          生成密钥对（私钥留在本机，公钥拷到被管控的机器）
              show   <解锁码>                显示这份解锁码要授权什么
              sign   <解锁码> [--key <私钥>] 签发令牌
              verify <令牌>   [--公钥]       校验令牌

            解锁码和令牌也可以用管道送进来：

              sentinel-unlock sign < request.txt

            私钥也可以放在别的盘上，比如 U 盘：

              sentinel-unlock sign REQ1.xxx --key /media/usb/unlock-private.pem
            """);

        return 0;
    }
}
