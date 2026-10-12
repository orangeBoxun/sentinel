using System.Diagnostics;

namespace Sentinel.Setup;

/// <summary>
/// 跑一个 PowerShell 脚本，把输出一行一行交出来。
///
/// <para>
/// 单独抽出来是为了**能被验证**：安装程序自己一启动就要管理员（UAC 弹窗），没法在自动化里
/// 点下去，但"参数怎么拼、输出怎么接"这段是可以拿一个假脚本测的 —— 而这段恰恰是容易错的地方。
/// </para>
/// <para>
/// 参数写成"名字 + 值"的元组，<b>值为空就是开关型参数</b>（<c>-Purge</c>）：直接往命令行里塞一个
/// 空字符串的话，PowerShell 会把它当成一个位置参数，脚本收到的就不是你写的那样。
/// </para>
/// </summary>
public static class ScriptRunner
{
    public static async Task<int> RunAsync(
        string script, (string Name, string? Value)[] arguments, Action<string>? onLine = null)
    {
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(script);

        foreach (var (name, value) in arguments)
        {
            start.ArgumentList.Add(name);

            if (!string.IsNullOrEmpty(value))
                start.ArgumentList.Add(value);
        }

        using var process = Process.Start(start)!;

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) onLine?.Invoke(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) onLine?.Invoke(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // ConfigureAwait(false)：调用方要是阻塞着等（比如检查脚本里 GetResult()），
        // 这里再回到原线程就是死锁 —— 而那个线程正卡在等它。
        await process.WaitForExitAsync().ConfigureAwait(false);

        return process.ExitCode;
    }
}
