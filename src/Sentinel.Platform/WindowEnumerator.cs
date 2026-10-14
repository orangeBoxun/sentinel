using System.Text;
using Sentinel.Core.Abstractions;
using Sentinel.Core.Models;

namespace Sentinel.Platform;

/// <summary>
/// Enumerates top-level windows in the current session.
///
/// Uses Win32 EnumWindows rather than UIA's desktop FindAllChildren. UIA enumeration reads several
/// properties per window, and each property is a separate cross-process round trip; with ~13 windows
/// that measured 1.5–2.1 s per pass — more than the content reads it exists to feed, and the single
/// biggest contributor to reaction latency. The Win32 calls below are local and cost almost nothing.
/// UIA is then used only for the windows that actually match a target.
/// </summary>
public sealed class WindowEnumerator : IWindowEnumerator, IDisposable
{
    private readonly bool _includeExecutablePath;

    /// <param name="includeExecutablePath">
    /// Resolving each window's .exe path opens the target process and reads its module list, which
    /// blocks on protected processes (security software, sandboxed apps). Matching only needs the
    /// process name, so the monitor leaves this off; the UI's target picker turns it on.
    /// </param>
    public WindowEnumerator(bool includeExecutablePath = false)
    {
        _includeExecutablePath = includeExecutablePath;
    }

    public IReadOnlyList<WindowRef> Enumerate()
    {
        var results = new List<WindowRef>();
        var foreground = NativeMethods.GetForegroundWindow();

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            // The alt-tab window set: visible, unowned, captioned, and not a tool window. Skips the
            // taskbar, the desktop, hidden helpers, and owned dialogs belonging to a window we
            // already report.
            if (!NativeMethods.IsWindowVisible(hwnd))
                return true;

            if (NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) != 0)
                return true;

            if ((NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE) & NativeMethods.WS_EX_TOOLWINDOW) != 0)
                return true;

            if (IsShellWindow(hwnd))
                return true;

            var length = NativeMethods.GetWindowTextLength(hwnd);
            if (length <= 0)
                return true;

            var title = new StringBuilder(length + 1);
            NativeMethods.GetWindowText(hwnd, title, title.Capacity);

            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0)
                return true;

            var (processName, exePath) = DescribeProcess((int)pid, _includeExecutablePath);
            var minimized = NativeMethods.IsIconic(hwnd);

            results.Add(new WindowRef((int)pid, processName, title.ToString(), exePath, hwnd,
                minimized,
                hwnd == foreground,
                IsExposedOnScreen(hwnd, minimized)));
            return true;
        }, 0);

        return results;
    }

    /// <summary>
    /// 这个窗口有没有<b>一块真的露在屏幕上</b>。
    ///
    /// <para>
    /// 判据是采样：在窗口矩形里撒一个网格，看每个点底下那个顶层窗口是不是它自己。有一个点是它
    /// 就说明露着；全都落在别人身上，就是被完全盖住了。
    /// </para>
    /// <para>
    /// 为什么不能只看 <c>IsWindowVisible</c>：它只说"没被隐藏" —— 一个被最大化窗口完全盖住的
    /// 窗口照样返回 true。而"看得见"才是该不该管的理由：看不见的东西影响不到任何人，
    /// 读它只是白花 UIA 的时间（一次几百毫秒）。反过来，**露着就得管** ——
    /// 否则摆一个小窗口挡在前面，旁边的违规视频就永远没人看了。
    /// </para>
    /// <para>
    /// 采样有精度：露出来的那条边细到不足窗口的六分之一左右时可能采不到。真要那种精度，
    /// 把网格加密就行 —— 每个采样点只是一次本地调用，比一次 UIA 读取便宜三个数量级。
    /// </para>
    /// </summary>
    private static bool IsExposedOnScreen(nint hwnd, bool minimized)
    {
        if (minimized)
            return false;   // 收起来的窗口一点都不露

        if (!NativeMethods.GetWindowRect(hwnd, out var rect))
            return false;

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;

        if (width <= 0 || height <= 0)
            return false;

        // 7×7，从离边 2 像素到离边 2 像素 —— 避开圆角/阴影那道边，又不至于漏掉贴边露出来的部分。
        const int steps = 7;

        for (var row = 0; row < steps; row++)
        {
            var y = rect.Top + 2 + Math.Max(0, height - 4) * row / (steps - 1);

            for (var column = 0; column < steps; column++)
            {
                var x = rect.Left + 2 + Math.Max(0, width - 4) * column / (steps - 1);

                var hit = NativeMethods.WindowFromPoint(new NativeMethods.POINT(x, y));

                if (hit != 0 && NativeMethods.GetAncestor(hit, NativeMethods.GA_ROOT) == hwnd)
                    return true;
            }
        }

        return false;
    }

    /// <summary>The shell's own windows pass the alt-tab test but are not application windows.</summary>
    private static bool IsShellWindow(nint hwnd)
    {
        var buffer = new StringBuilder(64);
        if (NativeMethods.GetClassName(hwnd, buffer, buffer.Capacity) <= 0)
            return false;

        var className = buffer.ToString();
        return className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    private static (string ProcessName, string ExePath) DescribeProcess(int pid, bool includePath)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);

            if (!includePath)
                return (process.ProcessName, "");

            string exe = "";
            try
            {
                exe = process.MainModule?.FileName ?? "";
            }
            catch
            {
                // Access denied for elevated/system processes; the process name is enough.
            }

            return (process.ProcessName, exe);
        }
        catch
        {
            return ("?", "");
        }
    }

    public void Dispose()
    {
        // Nothing unmanaged is held any more; kept so callers' using-blocks stay valid.
    }
}
