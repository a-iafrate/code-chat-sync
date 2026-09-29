using CodeChatSync.App.Services;
using CodeChatSync.Core;
using H.NotifyIcon;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CodeChatSync.App;

/// <summary>
/// Tray presence for the app: a menu to sync on demand, open the status window, or
/// exit, plus notifications when a run needs attention.
/// </summary>
public sealed class TrayIconHost : IDisposable
{
    private readonly SyncHost _syncHost;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly TaskbarIcon _trayIcon;
    private readonly MenuFlyoutItem _syncNowItem;
    private readonly MenuFlyoutItem _statusItem;
    private readonly ToggleMenuFlyoutItem _autoStartItem;
    private readonly AutoStartManager _autoStart = new(new RegistryStartupEntryStore());

    private bool _disposed;

    public TrayIconHost(SyncHost syncHost, DispatcherQueue dispatcherQueue)
    {
        _syncHost = syncHost ?? throw new ArgumentNullException(nameof(syncHost));
        _dispatcherQueue = dispatcherQueue ?? throw new ArgumentNullException(nameof(dispatcherQueue));

        _syncNowItem = new MenuFlyoutItem { Text = "Sync now" };
        _syncNowItem.Click += async (_, _) => await SyncNowAsync();

        _statusItem = new MenuFlyoutItem { Text = "Waiting for Visual Studio to close" , IsEnabled = false };

        var openItem = new MenuFlyoutItem { Text = "Open CodeChatSync" };
        openItem.Click += (_, _) => ShowWindowRequested?.Invoke(this, EventArgs.Empty);

        _autoStartItem = new ToggleMenuFlyoutItem { Text = "Start with Windows" };
        _autoStartItem.Click += OnAutoStartToggled;

        var exitItem = new MenuFlyoutItem { Text = "Exit" };
        exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        var menu = new MenuFlyout();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(_syncNowItem);
        menu.Items.Add(openItem);
        menu.Items.Add(_autoStartItem);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(exitItem);

        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "CodeChatSync",
            ContextFlyout = menu,
            NoLeftClickDelay = true
        };

        _trayIcon.LeftClickCommand = new RelayCommand(() => ShowWindowRequested?.Invoke(this, EventArgs.Empty));

        _syncHost.SyncCompleted += OnSyncCompleted;
        _syncHost.ProviderRunningChanged += OnProviderRunningChanged;

        _trayIcon.ForceCreate();
        RefreshAutoStart();
        UpdateStatus(_syncHost.IsProviderRunning ? "Visual Studio is open" : "Waiting for Visual Studio to close");
    }

    /// <summary>Raised when the user asks to see the status window.</summary>
    public event EventHandler? ShowWindowRequested;

    /// <summary>Raised when the user asks to close the app.</summary>
    public event EventHandler? ExitRequested;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _syncHost.SyncCompleted -= OnSyncCompleted;
        _syncHost.ProviderRunningChanged -= OnProviderRunningChanged;

        // Removes the icon straight away instead of leaving a dead one in the tray.
        _trayIcon.Dispose();
    }

    private async Task SyncNowAsync()
    {
        _syncNowItem.IsEnabled = false;
        UpdateStatus("Syncing...");

        try
        {
            var outcome = await _syncHost.SyncNowAsync();
            Notify(outcome);
        }
        finally
        {
            _syncNowItem.IsEnabled = true;
        }
    }

    private void OnSyncCompleted(object? sender, SyncOutcome outcome) =>
        _dispatcherQueue.TryEnqueue(() => Notify(outcome));

    private void OnProviderRunningChanged(object? sender, bool isRunning) =>
        _dispatcherQueue.TryEnqueue(() => UpdateStatus(
            isRunning ? "Visual Studio is open" : "Waiting for Visual Studio to close"));

    private void OnAutoStartToggled(object sender, RoutedEventArgs e)
    {
        try
        {
            _autoStart.Set(_autoStartItem.IsChecked, ExecutablePath);
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            _trayIcon.ShowNotification("CodeChatSync", $"Could not change auto-start: {exception.Message}");
            RefreshAutoStart();
        }
    }

    /// <summary>Reads the real state, so an entry changed outside the app still shows correctly.</summary>
    private void RefreshAutoStart()
    {
        try
        {
            _autoStartItem.IsChecked = _autoStart.IsEnabled(ExecutablePath);
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            _autoStartItem.IsChecked = false;
            _autoStartItem.IsEnabled = false;
        }
    }

    /// <summary>Path registered for auto-start: the running executable, not its DLL.</summary>
    private static string ExecutablePath =>
        Environment.ProcessPath ?? System.Reflection.Assembly.GetExecutingAssembly().Location;

    private void Notify(SyncOutcome outcome)
    {
        UpdateStatus(outcome.Summary);

        // Only interrupt the user when something actually needs them.
        if (outcome.NeedsAttention)
        {
            _trayIcon.ShowNotification("CodeChatSync", outcome.Summary);
        }
    }

    private void UpdateStatus(string status)
    {
        _statusItem.Text = status;
        _trayIcon.ToolTipText = $"CodeChatSync - {status}";
    }

    /// <summary>Minimal command so the tray icon can react to a left click.</summary>
    private sealed class RelayCommand(Action execute) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => execute();

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
