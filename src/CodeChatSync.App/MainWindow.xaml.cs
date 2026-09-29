using CodeChatSync.App.Services;
using CodeChatSync.Core;
using CodeChatSync.Git;
using CodeChatSync.Providers.VisualStudio;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace CodeChatSync.App;

public sealed partial class MainWindow : Window
{
    private readonly SyncHost _syncHost;
    private string? _loadedFolder;
    private bool _loadingSettings;
    private readonly List<(ProjectIdentity Project, CheckBox All, List<(string Id, CheckBox Selected)> Sessions)> _sessionGroups = [];

    public MainWindow(SyncHost syncHost)
    {
        _syncHost = syncHost ?? throw new ArgumentNullException(nameof(syncHost));

        InitializeComponent();

        if (File.Exists(AppIcons.AppIconPath))
        {
            AppWindow.SetIcon(AppIcons.AppIconPath);
        }

        _syncHost.SyncCompleted += OnSyncCompleted;
        _syncHost.ProviderRunningChanged += OnProviderRunningChanged;
        Closed += OnClosed;

        UpdateProviderStatus(_syncHost.IsProviderRunning);

        if (_syncHost.LastOutcome is { } lastOutcome)
        {
            ShowOutcome(lastOutcome);
        }

        _ = LoadSettingsAsync();
    }

    private async Task<bool> LoadSettingsAsync()
    {
        SaveSettingsButton.IsEnabled = false;
        SaveSelectionButton.IsEnabled = false;
        _loadingSettings = true;
        SessionSelectionPanel.Children.Clear();
        _sessionGroups.Clear();
        try
        {
            var config = LocalConfig.Load();
            _loadedFolder = config.SyncRootPath;
            SyncFolderTextBox.Text = config.SyncRootPath ?? string.Empty;
            RemoteTextBox.Text = string.Empty;
            if (config.SyncRootPath is not { Length: > 0 } folder)
            {
                ShowSettingsMessage("Choose a folder for your private sync repository.", InfoBarSeverity.Informational);
                return true;
            }

            if (!Directory.Exists(folder))
            {
                ShowSettingsMessage("The configured sync folder is missing. Choose an existing folder or initialize it.", InfoBarSeverity.Warning);
                return true;
            }

            var repository = new SyncRepository(folder);
            var status = await Task.Run(() => repository.GetStatus());
            if (status.IsGitRepository)
            {
                RemoteTextBox.Text = await Task.Run(repository.GetOriginRemoteUrl) ?? string.Empty;
            }
            else
            {
                ShowSettingsMessage("This folder is not a Git repository. Check Initialize to add an origin remote.", InfoBarSeverity.Warning);
            }

            await LoadSessionsAsync(config, folder);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or GitCommandException or NotSupportedException)
        {
            ShowSettingsMessage($"Could not load settings: {exception.Message}", InfoBarSeverity.Error);
            return false;
        }
        finally
        {
            _loadingSettings = false;
            SaveSettingsButton.IsEnabled = true;
        }
    }

    private void OnSyncFolderChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loadingSettings && _loadedFolder is not null
            && !string.Equals(SyncFolderTextBox.Text, _loadedFolder, StringComparison.OrdinalIgnoreCase))
        {
            RemoteTextBox.Text = string.Empty;
        }
    }

    private async void OnBrowseFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
            {
                SyncFolderTextBox.Text = folder.Path;
            }
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException
            or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowSettingsMessage($"Could not choose a folder: {exception.Message}", InfoBarSeverity.Error);
        }
    }

    private async void OnSaveSettingsClick(object sender, RoutedEventArgs e)
    {
        var folder = SyncFolderTextBox.Text;
        if (string.IsNullOrWhiteSpace(folder))
        {
            ShowSettingsMessage("Choose a sync folder before saving.", InfoBarSeverity.Error);
            return;
        }

        SaveSettingsButton.IsEnabled = false;
        SettingsProgress.IsActive = true;
        try
        {
            var saved = await _syncHost.SaveSettingsAsync(folder, RemoteTextBox.Text, InitializeCheckBox.IsChecked == true);
            if (!saved)
            {
                ShowSettingsMessage("A sync is in progress. Try saving again when it finishes.", InfoBarSeverity.Warning);
                return;
            }

            InitializeCheckBox.IsChecked = false;
            if (await LoadSettingsAsync())
            {
                ShowSettingsMessage("Settings saved. Sync will use this folder on the next run.", InfoBarSeverity.Success);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or GitCommandException or NotSupportedException)
        {
            ShowSettingsMessage($"Could not save settings: {exception.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            SaveSettingsButton.IsEnabled = true;
            SettingsProgress.IsActive = false;
        }
    }

    private async Task LoadSessionsAsync(LocalConfig config, string syncRoot)
    {
        var groups = await Task.Run(() =>
        {
            var shared = SharedConfig.Load(syncRoot);
            var results = new List<(ProjectIdentity Project, string Name, LocalRestoreSelection? Selection, IReadOnlyList<(string Id, string Title)> Sessions)>();
            foreach (var entry in config.Projects)
            {
                if (!ProjectIdentity.TryFromRemote(entry.Remote, out var identity) || identity is null)
                {
                    throw new InvalidDataException($"Invalid registered project remote: {entry.Remote}");
                }

                var name = shared.Find(identity)?.Name ?? identity.Slug;
                if (name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                    || name.Contains('/') || name.Contains('\\'))
                {
                    throw new InvalidDataException($"Invalid shared project folder name: {name}");
                }

                var projectRoot = Path.Combine(syncRoot, "visualstudio", name);
                var sessions = CopilotChatDiscovery.DiscoverSessions(projectRoot)
                    .Select(session => (Id: Path.GetFileName(session.SessionDirectory), Title: session.Name ?? session.Id))
                    .ToArray();
                results.Add((identity, name, config.FindRestoreSelection("visualstudio", identity), sessions));
            }

            return results;
        });

        if (groups.Count == 0)
        {
            SessionSelectionPanel.Children.Add(new TextBlock { Text = "No projects are registered on this PC. Register one with 'codechatsync add'.", TextWrapping = TextWrapping.Wrap });
            return;
        }

        foreach (var group in groups)
        {
            var container = new StackPanel { Spacing = 4 };
            container.Children.Add(new TextBlock { Text = group.Name, FontSize = 17 });
            var all = new CheckBox { Content = "Restore all chats (including future chats)", IsChecked = group.Selection is null };
            container.Children.Add(all);
            var sessionPanel = new StackPanel { Spacing = 2, Margin = new Microsoft.UI.Xaml.Thickness(20, 0, 0, 0) };
            all.Checked += (_, _) => SetSessionInputsEnabled(sessionPanel, false);
            all.Unchecked += (_, _) => SetSessionInputsEnabled(sessionPanel, true);
            var items = new List<(string Id, CheckBox Selected)>();
            foreach (var session in group.Sessions)
            {
                var selected = new CheckBox
                {
                    Content = $"{session.Title} ({session.Id})",
                    IsChecked = group.Selection?.SessionIds.Contains(session.Id, StringComparer.OrdinalIgnoreCase) ?? true,
                    IsEnabled = all.IsChecked != true
                };
                sessionPanel.Children.Add(selected);
                items.Add((session.Id, selected));
            }

            if (items.Count == 0)
            {
                sessionPanel.Children.Add(new TextBlock { Text = "No synced chats for this project yet." });
            }

            container.Children.Add(sessionPanel);
            SessionSelectionPanel.Children.Add(container);
            _sessionGroups.Add((group.Project, all, items));
        }

        SaveSelectionButton.IsEnabled = true;
    }

    private static void SetSessionInputsEnabled(StackPanel panel, bool enabled)
    {
        foreach (var child in panel.Children.OfType<CheckBox>())
        {
            child.IsEnabled = enabled;
        }
    }

    private async void OnSaveSelectionClick(object sender, RoutedEventArgs e)
    {
        var selections = _sessionGroups.Select(group =>
            (group.Project, SessionIds: group.All.IsChecked == true
                ? (IReadOnlyList<string>?)null
                : group.Sessions.Where(session => session.Selected.IsChecked == true)
                    .Select(session => session.Id).ToArray())).ToArray();

        SaveSelectionButton.IsEnabled = false;
        try
        {
            if (!await _syncHost.SaveRestoreSelectionsAsync(selections))
            {
                ShowSelectionMessage("A sync is in progress. Try again when it finishes.", InfoBarSeverity.Warning);
                return;
            }

            ShowSelectionMessage("Saved for this PC. Unselected chats remain in the Git repository; existing local chats are not deleted.", InfoBarSeverity.Success);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or InvalidDataException or NotSupportedException)
        {
            ShowSelectionMessage($"Could not save chat selection: {exception.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            SaveSelectionButton.IsEnabled = true;
        }
    }

    private void ShowSelectionMessage(string message, InfoBarSeverity severity)
    {
        SelectionInfoBar.Message = message;
        SelectionInfoBar.Severity = severity;
        SelectionInfoBar.IsOpen = true;
    }

    private void ShowSettingsMessage(string message, InfoBarSeverity severity)
    {
        SettingsInfoBar.Message = message;
        SettingsInfoBar.Severity = severity;
        SettingsInfoBar.IsOpen = true;
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
