namespace Sentinel.Platform;

/// <summary>
/// Query and change a window's visible state. Exists so action tests can assert objectively
/// ("is it actually minimized?") instead of relying on someone watching the screen.
/// </summary>
public static class WindowState
{
    public static bool Exists(long handle) => handle != 0 && NativeMethods.IsWindow((nint)handle);

    public static bool IsMinimized(long handle) => handle != 0 && NativeMethods.IsIconic((nint)handle);

    public static bool IsMaximized(long handle) => handle != 0 && NativeMethods.IsZoomed((nint)handle);

    public static bool IsVisible(long handle) => handle != 0 && NativeMethods.IsWindowVisible((nint)handle);

    public static void Restore(long handle)
    {
        if (handle != 0)
            NativeMethods.ShowWindow((nint)handle, NativeMethods.SW_RESTORE);
    }
}
