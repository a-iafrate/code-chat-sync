using CodeChatSync.App.Services;
using CodeChatSync.Core;
using Microsoft.UI.Xaml;

namespace CodeChatSync.App;

public sealed partial class MainWindow : Window
{
    private readonly SyncHost _syncHost;

    public MainWindow(SyncHost syncHost)
    {
        _syncHost = syncHost ?? throw new ArgumentNullException(nameof(syncHost));

        InitializeComponent();

        _syncHost.SyncCompleted += OnSyncCompleted;
        _syncHost.ProviderRunningChanged += OnProviderRunningChanged;
        Closed += OnClosed;

        UpdateProviderStatus(_syncHost.IsProviderRunning);

        if (_syncHost.LastOutcome is { } lastOutcome)
        {
            ShowOutcome(lastOutcome);
        }
    }

    private async void OnSyncNowClick(object sender, RoutedEventArgs e)
    {
        SyncNowButton.IsEnabled = false;
        SyncProgress.IsActive = true;

        try
        {
            ShowOutcome(await _syncHost.SyncNowAsync());
        }
        finally
        {
            SyncProgress.IsActive = false;
            SyncNowButton.IsEnabled = true;
        }
    }

    private void OnSyncCompleted(object? sender, SyncOutcome outcome) =>
        DispatcherQueue.TryEnqueue(() => ShowOutcome(outcome));

    private void OnProviderRunningChanged(object? sender, bool isRunning) =>
        DispatcherQueue.TryEnqueue(() => UpdateProviderStatus(isRunning));

    private void UpdateProviderStatus(bool isProviderRunning) =>
        ProviderStatusText.Text = isProviderRunning
            ? "Visual Studio is open. Chats will sync once it closes."
            : "Visual Studio is closed. Watching for new chats.";

    private void ShowOutcome(SyncOutcome outcome) =>
        LastRunText.Text = $"{DateTime.Now:t} - {outcome.Status}: {outcome.Summary}";

    /// <summary>
    /// The app lives in the tray, so closing this window only detaches it from the
    /// sync host rather than stopping the watch.
    /// </summary>
    private void OnClosed(object sender, WindowEventArgs args)
    {
        _syncHost.SyncCompleted -= OnSyncCompleted;
        _syncHost.ProviderRunningChanged -= OnProviderRunningChanged;
        Closed -= OnClosed;
    }
}
