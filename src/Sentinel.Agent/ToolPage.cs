using System.Diagnostics;
using System.IO;

namespace Sentinel.Agent;

/// <summary>
/// 两个本地 HTML 工具 —— 界面上按钮点开的就是它们。
///
/// <para>
/// 它们是**两件不同的事**，所以分成两个文件，而不是挤在一个页面里：
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>生成密钥对</b>（<c>sentinel-keygen.html</c>）：一辈子做一次的设置步骤。
/// </description></item>
/// <item><description>
/// <b>签名换令牌</b>（<c>sentinel-sign.html</c>）：每次想放宽限制都要走的一步。
/// </description></item>
/// </list>
/// <para>
/// 文件随安装走（放在程序自己的目录里），所以仓库被挪走或删掉都不影响用 —— 以前它们只存在于
/// 仓库的 <c>tools/</c> 下，那等于把"想放宽限制时唯一要走的一步"挂在一个随时会消失的地方。
/// </para>
/// </summary>
internal static class ToolPage
{
    private const string KeygenFile = "sentinel-keygen.html";
    private const string SignFile = "sentinel-sign.html";

    /// <summary>生成密钥对 —— 只在开启解锁保护那一步用得到。</summary>
    public static bool TryOpenKeygen(out string? error) => TryOpen(KeygenFile, out error);

    /// <summary>签名换令牌 —— 每次要令牌的时候用。</summary>
    public static bool TryOpenSign(out string? error) => TryOpen(SignFile, out error);

    /// <summary>交给默认浏览器。打不开时返回 false 并说明原因 —— 静默什么都不发生最糟。</summary>
    private static bool TryOpen(string fileName, out string? error)
    {
        error = null;
        var full = Path.Combine(AppContext.BaseDirectory, fileName);

        if (!File.Exists(full))
        {
            error = $"找不到 {fileName}。它本该和程序放在一起（{AppContext.BaseDirectory}）—— 重新安装一次就有。";
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(full) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            error = $"打不开浏览器：{ex.Message}";
            return false;
        }
    }
}
