using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Sentinel.Agent;

/// <summary>
/// The application mark, from the one .ico embedded in this assembly.
///
/// <para>
/// Two toolkits want it as two different types — WPF windows take an <see cref="ImageSource"/>, the
/// WinForms tray takes a <see cref="System.Drawing.Icon"/> — so both come from here. Otherwise the
/// exe, the windows and the tray are three places to keep in agreement about what this program looks
/// like, and they had already drifted: the tray was showing the stock Windows shield.
/// </para>
/// </summary>
internal static class AppIcon
{
    // Named explicitly rather than "pack://application:,,,/Assets/…": the bare form resolves against
    // whichever assembly started the process, so it works in the Agent and throws everywhere else —
    // including the preview harness, which is exactly where the UI gets looked at.
    private const string Path = "pack://application:,,,/Sentinel.Agent;component/Assets/sentinel.ico";

    /// <summary>For <c>Window.Icon</c>.</summary>
    public static ImageSource ForWindow() =>
        BitmapFrame.Create(new Uri(Path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

    /// <summary>
    /// For the tray. <paramref name="size"/> asks for a specific entry in the .ico rather than letting
    /// the shell scale one — at 16px a downscaled 32px image is visibly soft.
    /// </summary>
    public static System.Drawing.Icon ForTray(int size = 16)
    {
        // Deliberately not in a using: Icon reads from this stream for the life of the icon, and a
        // disposed one leaves a tray icon that draws nothing. A MemoryStream needs no cleanup here.
        var stream = new MemoryStream();
        using (var source = Application.GetResourceStream(new Uri(Path))!.Stream)
            source.CopyTo(stream);
        stream.Position = 0;

        return new System.Drawing.Icon(stream, size, size);
    }
}
