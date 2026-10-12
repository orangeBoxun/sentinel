using System.Diagnostics;
using System.IO;
using Sentinel.Platform;

namespace Sentinel.Preview;

/// <summary>
/// 试「服务结束不再心跳的 Agent」那件事 —— 它**会杀进程**，所以三道防线都要验：
/// 名字对、路径一字不差、活得够久。
///
/// <para>
/// 错杀一个用户进程比不杀严重得多，而这段代码平时根本不会跑（只有 Agent 卡住时才走到），
/// 所以它是最容易"写完就没人再看一眼"的那类。这里拿一个**看着像 Agent、其实完全无害**的东西来试：
/// 把 <c>cmd.exe</c> 复制成 <c>Sentinel.Agent.exe</c>，让它在那儿等着。
/// </para>
/// </summary>
internal static class KillCheck
{
    public static int Run(string workDir)
    {
        // 传进来的路径常常是正斜杠（C:/Users/...）。CreateProcess 对带正斜杠的文件名会失败，
        // 而报出来的是"刚起来就退了（退出码 1）"—— 和"程序自己出错了"长得一模一样，
        // 能查半天。GetFullPath 顺手把它换成反斜杠。
        workDir = Path.GetFullPath(workDir);
        Directory.CreateDirectory(workDir);

        var ok = true;
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var session = Process.GetCurrentProcess().SessionId;

        Console.WriteLine($"当前会话: {session}");
        Console.WriteLine();

        // 一个"假 Agent"：名字对、路径对。
        var fakeAgent = Path.Combine(workDir, "Sentinel.Agent.exe");
        File.Copy(cmd, fakeAgent, overwrite: true);

        // 另一个名字一样、**路径不一样**的。它绝不能被碰。
        var otherDir = Path.Combine(workDir, "别处");
        Directory.CreateDirectory(otherDir);
        var sameNameElsewhere = Path.Combine(otherDir, "Sentinel.Agent.exe");
        File.Copy(cmd, sameNameElsewhere, overwrite: true);

        using var ours = StartQuiet(fakeAgent, out var ourError);
        using var theirs = StartQuiet(sameNameElsewhere, out var theirError);

        if (ours is null || theirs is null)
        {
            Console.WriteLine($"起不来测试进程：{ourError ?? theirError}");
            return 1;
        }

        try
        {
            // ---- 防线一：活得不够久的不杀（刚拉起来的那个还在初始化）
            var young = SessionLauncher.StopStaleAgentsIn(session, fakeAgent, TimeSpan.FromHours(1), out _);

            Report(ref ok, "刚起来的（年龄 < 门槛）不杀", young == 0, $"杀掉了 {young} 个");

            // ---- 防线二：名字一样但路径不一样的不杀
            var byPath = SessionLauncher.StopStaleAgentsIn(session, fakeAgent, TimeSpan.Zero, out _);

            Report(ref ok, "只杀路径对得上的那个", byPath == 1, $"杀掉了 {byPath} 个（应为 1）");
            Report(ref ok, "路径不一样的那个还活着", !theirs.HasExited, theirs.HasExited ? "**被误杀了**" : "活着");

            // ---- 防线三：该杀的时候真的杀得掉
            ours.WaitForExit(5000);
            Report(ref ok, "该杀的那个真的结束了", ours.HasExited, ours.HasExited ? "已结束" : "**还活着**");

            // ---- 幂等：已经没了，再问一次不该报错也不该算数
            var again = SessionLauncher.StopStaleAgentsIn(session, fakeAgent, TimeSpan.Zero, out var error);

            Report(ref ok, "已经没了的时候不报错", again == 0 && error is null,
                $"返回 {again}，错误 {(error ?? "(没有)")}");
        }
        finally
        {
            foreach (var process in new[] { ours, theirs })
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(3000);
                    }
                }
                catch
                {
                    // 测试的收尾，出错也无所谓。
                }
            }

            TryDelete(workDir);
        }

        Console.WriteLine();
        Console.WriteLine(ok
            ? "结论：该杀的杀得掉，不该杀的碰都不碰。"
            : "结论：有一样不对 —— 这条会杀进程，别放过。");

        return ok ? 0 : 1;
    }

    /// <summary>
    /// 起一个 cmd.exe 的副本，让它待着别退。
    ///
    /// <para>
    /// 用 <c>/c ping</c> 而不是 <c>/k</c>：这是个 GUI 程序起的子进程，没有控制台，而
    /// <c>/k</c> 在输入被重定向成管道之后会立刻读到 EOF 直接退出。ping 自己会跑几百秒，
    /// 正好当个"活着但什么也不干"的进程。
    /// </para>
    /// </summary>
    private static Process? StartQuiet(string exe, out string? error)
    {
        error = null;

        try
        {
            // 不重定向任何东西：这个进程只是用来"占着名字和路径"的，输出没人看，
            // 而重定向会引入一堆无关的坑（管道缓冲、"同步异步不能混用"之类）。
            var process = Process.Start(new ProcessStartInfo(exe, "/c ping -n 300 127.0.0.1")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null)
            {
                error = "Process.Start 返回了 null";
                return null;
            }

            // 等它真的起来 —— 不然下面算年龄的时候它还没开始跑。
            Thread.Sleep(900);

            if (process.HasExited)
            {
                error = $"刚起来就退了（退出码 {process.ExitCode}）";
                return null;
            }

            return process;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    private static void TryDelete(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // 文件还被占着就算了，临时目录而已。
        }
    }

    private static void Report(ref bool ok, string what, bool good, string detail)
    {
        Console.WriteLine($"{(good ? "✓" : "✗")} {what}：{detail}");
        ok &= good;
    }
}
