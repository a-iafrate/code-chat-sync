using CodeChatSync.App.Services;
using CodeChatSync.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace CodeChatSync.App;

public partial class App : Application
{
    private SyncHost? _syncHost;
    private TrayIconHost? _trayIcon;
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();

        // The default shuts the app down when the last window closes; this app lives
        // in the tray and must keep running until Exit is chosen from its menu.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _syncHost = new SyncHost();
        _syncHost.Start();

        _trayIcon = new TrayIconHost(_syncHost, DispatcherQueue.GetForCurrentThread());
        _trayIcon.ShowWindowRequested += (_, _) => ShowWindow();
        _trayIcon.ExitRequested += async (_, _) => await ExitAsync();

        // A fresh install would otherwise only show a tray icon, leaving a new user
        // with no hint of what to do; the window opens the setup guide.
        if (IsFirstRun())
        {
            ShowWindow();
        }
    }

    private static bool IsFirstRun()
    {
        try
        {
            return GettingStarted.ShouldOpenWizardAutomatically(LocalConfig.Load());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>
    /// Shows the status window, recreating it when the user closed it earlier. The
    /// app keeps running in the tray, so closing the window must not end the process.
    /// </summary>
    private void ShowWindow()
    {
        if (_window is null)
        {
            _window = new MainWindow(_syncHost!);
            _window.Closed += (_, _) => _window = null;
        }

        _window.Activate();

        // Activate() on its own leaves the window behind the other apps when the
        // request comes from the tray, which looks like nothing happened.
        ForegroundWindow.Bring(_window);
    }

    private async Task ExitAsync()
    {
        _trayIcon?.Dispose();
        _trayIcon = null;

        if (_syncHost is not null)
        {
            await _syncHost.DisposeAsync();
            _syncHost = null;
        }

        _window?.Close();
        Exit();
    }
}
