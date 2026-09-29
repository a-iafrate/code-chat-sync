using System.Runtime.InteropServices;

namespace CodeChatSync.App.Services;

/// <summary>
/// Locates the icon files that ship next to the executable and loads them at the
/// size Windows actually asks for.
/// </summary>
internal static class AppIcons
{
    private const int SmallIconWidth = 49;
    private const int SmallIconHeight = 50;

    /// <summary>Multi-resolution icon used for the window and the taskbar.</summary>
    public static string AppIconPath { get; } = Resolve("app-icon.ico");

    /// <summary>Simplified glyph used in the notification area.</summary>
    public static string TrayIconPath { get; } = Resolve("tray-icon.ico");

    /// <summary>
    /// Loads the tray icon at the current small-icon size. The .ico holds several
    /// resolutions, so asking for the exact size lets Windows pick the right frame
    /// instead of scaling a larger one and producing a blurry icon on high DPI.
    /// </summary>
    public static System.Drawing.Icon? LoadTrayIcon()
    {
        if (!File.Exists(TrayIconPath))
        {
            return null;
        }

        var width = GetSystemMetrics(SmallIconWidth);
        var height = GetSystemMetrics(SmallIconHeight);

        return width > 0 && height > 0
            ? new System.Drawing.Icon(TrayIconPath, width, height)
            : new System.Drawing.Icon(TrayIconPath);
    }

    private static string Resolve(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Assets", fileName);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}
