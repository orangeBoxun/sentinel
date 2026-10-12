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
            results.Add(new WindowRef((int)pid, processName, title.ToString(), exePath, hwnd,
                NativeMethods.IsIconic(hwnd),
                hwnd == foreground));
            return true;
        }, 0);

        return results;
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
