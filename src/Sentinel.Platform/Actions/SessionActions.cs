using System.Diagnostics;
using System.Runtime.InteropServices;
using Sentinel.Core.Abstractions;
using Sentinel.Core.Models;

namespace Sentinel.Platform.Actions;

/// <summary>Minimizes the offending window (session-local, no elevation needed).</summary>
public sealed class MinimizeWindowAction : IAction
{
    public string Name => "minimize-window";

    public bool CanRun(ActionContext context) => context.Window.NativeHandle != 0;

    public void Execute(ActionContext context)
    {
        // ShowWindow reports failure by return value, not by throwing. Silently ignoring it is how
        // an action can be logged as "done" while the window never moved.
        if (!NativeMethods.ShowWindow((nint)context.Window.NativeHandle, NativeMethods.SW_MINIMIZE))
            throw new InvalidOperationException($"ShowWindow(SW_MINIMIZE) failed (win32 error {Marshal.GetLastWin32Error()})");
    }
}

/// <summary>Posts WM_CLOSE to the offending window (asks the app to close; app may prompt).</summary>
public sealed class CloseWindowAction : IAction
{
    public string Name => "close-window";

    public bool CanRun(ActionContext context) => context.Window.NativeHandle != 0;

    public void Execute(ActionContext context)
    {
        if (!NativeMethods.PostMessage((nint)context.Window.NativeHandle, NativeMethods.WM_CLOSE, 0, 0))
            throw new InvalidOperationException($"PostMessage(WM_CLOSE) failed (win32 error {Marshal.GetLastWin32Error()})");
    }
}

/// <summary>
/// Kills the whole target process. In v1 this runs session-local; the M4 Service path will
/// perform it with elevation so a lower-privilege target cannot be protected from it.
/// </summary>
public sealed class KillProcessAction : IAction
{
    public string Name => "kill-process";

    public bool CanRun(ActionContext context) => context.Window.ProcessId > 0;

    public void Execute(ActionContext context)
    {
        using var process = Process.GetProcessById(context.Window.ProcessId);
        process.Kill(entireProcessTree: true);
    }
}
