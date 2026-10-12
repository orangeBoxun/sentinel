using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Sentinel.Platform;

/// <summary>
/// Starts a process inside a logged-on user's session.
///
/// A service runs in session 0, which has no desktop — it cannot read UIA there, and anything it
/// starts inherits that. So the service's job is to put the Agent into the user's own session, where
/// the desktop actually is.
///
/// These calls need SeTcbPrivilege: they only work from a service running as SYSTEM.
/// </summary>
public static class SessionLauncher
{
    private const int WtsActive = 0;

    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;

    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;

    public static IReadOnlyList<int> ActiveSessionIds()
    {
        var ids = new List<int>();

        if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var buffer, out var count))
        {
            Debug.WriteLine($"WTSEnumerateSessions failed: {Marshal.GetLastWin32Error()}");
            return ids;
        }

        try
        {
            var size = Marshal.SizeOf<WtsSessionInfo>();
            for (var i = 0; i < count; i++)
            {
                var entry = Marshal.PtrToStructure<WtsSessionInfo>(buffer + i * size);
                if (entry.State == WtsActive && entry.SessionId != 0)
                    ids.Add(entry.SessionId);
            }
        }
        finally
        {
            WTSFreeMemory(buffer);
        }

        return ids;
    }

    /// <summary>Starts <paramref name="executable"/> on the desktop of <paramref name="sessionId"/>.</summary>
    public static bool TryStartInSession(int sessionId, string executable, string arguments, out string? error)
    {
        error = null;
        var token = IntPtr.Zero;
        var primary = IntPtr.Zero;
        var environment = IntPtr.Zero;

        try
        {
            if (!WTSQueryUserToken((uint)sessionId, out token))
            {
                error = $"WTSQueryUserToken({sessionId}) failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}";
                return false;
            }

            if (!DuplicateTokenEx(token, 0x02000000 /* MAXIMUM_ALLOWED */, IntPtr.Zero,
                    SecurityImpersonation, TokenPrimary, out primary))
            {
                error = $"DuplicateTokenEx failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}";
                return false;
            }

            var flags = CreateUnicodeEnvironment | CreateNoWindow;
            if (!CreateEnvironmentBlock(out environment, primary, false))
            {
                environment = IntPtr.Zero;   // not fatal: the process just inherits nothing useful
                flags = CreateNoWindow;
            }

            var startup = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() };

            if (!CreateProcessAsUser(primary, executable, $"\"{executable}\" {arguments}", IntPtr.Zero, IntPtr.Zero,
                    false, flags, environment, Path.GetDirectoryName(executable), ref startup, out var process))
            {
                error = $"CreateProcessAsUser failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}";
                return false;
            }

            CloseHandle(process.hThread);
            CloseHandle(process.hProcess);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            if (environment != IntPtr.Zero) DestroyEnvironmentBlock(environment);
            if (primary != IntPtr.Zero) CloseHandle(primary);
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }

    /// <summary>
    /// 结束某个会话里那个**已经不再心跳**的 Agent。返回结束了几个。
    ///
    /// <para>
    /// <b>为什么非做不可。</b>服务发现某个会话不再心跳时会去拉一个新的 Agent，而旧的进程
    /// 如果还活着，就还占着"只许开一个"的单实例互斥体 —— 新的启动即秒退。于是服务每 10 秒
    /// 拉一次、每次秒退，**永远拉不起来**。而界面看上去一切正常：托盘图标在、设置窗口能开、
    /// 进程也在跑，只是规则已经不生效了。这是唯一能让它恢复过来的动作。
    /// </para>
    /// <para>
    /// <b>三道防线，缺一不可</b>（错杀一个进程比不杀更糟）：
    /// 进程名要对、**可执行文件的完整路径要和我们要启动的那个一字不差**、
    /// 而且它已经活了超过 <paramref name="minimumAge"/>（刚起来还在初始化的不能算卡死）。
    /// 读不到路径就**不杀** —— 宁可恢复不了，也不杀错。
    /// </para>
    /// </summary>
    public static int StopStaleAgentsIn(int sessionId, string agentPath, TimeSpan minimumAge, out string? error)
    {
        error = null;

        var name = Path.GetFileNameWithoutExtension(agentPath);
        var stopped = 0;

        Process[] candidates;
        try
        {
            candidates = Process.GetProcessesByName(name);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return 0;
        }

        foreach (var process in candidates)
        {
            using (process)
            {
                try
                {
                    if (process.Id == Environment.ProcessId)
                        continue;

                    if (process.SessionId != sessionId)
                        continue;

                    // 路径对不上就不是我们要管的那个进程 —— 万一有别的程序也叫这个名字，
                    // 更不能杀。
                    var path = process.MainModule?.FileName;
                    if (!string.Equals(path, agentPath, StringComparison.OrdinalIgnoreCase))
                        continue;

                    // 刚拉起来的那个还在初始化，心跳还没来得及发 —— 那不是卡死。
                    if (DateTime.Now - process.StartTime < minimumAge)
                        continue;

                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                    stopped++;
                }
                catch (Exception ex)
                {
                    // 权限不够、或者它刚好自己退了。记下来，但不影响别的会话。
                    error = ex.Message;
                }
            }
        }

        return stopped;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WtsSessionInfo
    {
        public int SessionId;
        public IntPtr WinStationName;
        public int State;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version, out IntPtr sessions, out int count);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr existing, uint desiredAccess, IntPtr attributes,
        int impersonationLevel, int tokenType, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(IntPtr token, string? applicationName, string? commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags,
        IntPtr environment, string? currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
