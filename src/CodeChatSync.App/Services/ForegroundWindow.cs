using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace CodeChatSync.App.Services;

/// <summary>
/// Brings a window to the front.
/// <para>
/// <see cref="Window.Activate"/> alone is not enough when the request comes from the
/// tray icon: Windows refuses to change the foreground window for a process that does
/// not already own it, so the window is created and shown behind everything else and
/// the click looks like it did nothing. Briefly attaching to the foreground window's
/// input queue restores that permission for the duration of the call.
/// </para>
/// </summary>
internal static class ForegroundWindow
{
    private const int ShowRestore = 9;
    private const int Show = 5;

    public static void Bring(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        if (handle == IntPtr.Zero)
        {
            return;
        }

        // A window the user minimised earlier has to be restored, not just raised.
        ShowWindow(handle, IsIconic(handle) ? ShowRestore : Show);

        var foreground = GetForegroundWindow();
        if (foreground == handle)
        {
            return;
        }

        var currentThread = GetCurrentThreadId();
        var foregroundThread = GetWindowThreadProcessId(foreground, IntPtr.Zero);

        var attached = foregroundThread != 0
            && foregroundThread != currentThread
            && AttachThreadInput(currentThread, foregroundThread, true);

        try
        {
            BringWindowToTop(handle);
            SetForegroundWindow(handle);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(currentThread, foregroundThread, false);
            }
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint attachTo, uint attachFrom, [MarshalAs(UnmanagedType.Bool)] bool attach);
}
