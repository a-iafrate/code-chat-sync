using CodeChatSync.Core;
using CodeChatSync.Git;
using CodeChatSync.Providers.VisualStudio;

namespace CodeChatSync.App.Services;

/// <summary>
/// Composes the sync pieces for the tray app: it watches Visual Studio, syncs once
/// it has been closed long enough, and reports what happened.
/// </summary>
/// <remarks>
/// Configuration is re-read for every run, so changing the sync folder or
/// registering a project does not require restarting the app.
/// </remarks>
public sealed class SyncHost : IAsyncDisposable
{
    private readonly VisualStudioChatProvider _provider = new();
    private readonly SyncCoordinator _coordinator;
    private readonly ProviderWatcher _watcher;
    private readonly CancellationTokenSource _cancellation = new();

    private Task? _watching;

    public SyncHost(WatchOptions? watchOptions = null)
    {
        _coordinator = new SyncCoordinator(CreateOrchestrator);
        _watcher = new ProviderWatcher(new ProcessGuard(), _provider.ProcessNames, watchOptions);

        _coordinator.Completed += (_, outcome) => SyncCompleted?.Invoke(this, outcome);
        _watcher.ProviderRunningChanged += (_, running) => ProviderRunningChanged?.Invoke(this, running);
        _watcher.SyncRequested += OnSyncRequested;
    }

    /// <summary>Raised when a sync run finishes, from a background thread.</summary>
    public event EventHandler<SyncOutcome>? SyncCompleted;

    /// <summary>Raised when Visual Studio starts or stops.</summary>
    public event EventHandler<bool>? ProviderRunningChanged;

    /// <summary>Whether Visual Studio was running at the last check.</summary>
    public bool IsProviderRunning => _watcher.IsProviderRunning;

    /// <summary>Whether a sync is in progress.</summary>
    public bool IsSyncing => _coordinator.IsRunning;

    /// <summary>Outcome of the most recent sync, if any has run yet.</summary>
    public SyncOutcome? LastOutcome => _coordinator.LastOutcome;

    /// <summary>Starts watching for Visual Studio closing.</summary>
    public void Start() => _watching ??= _watcher.WatchAsync(_cancellation.Token);

    /// <summary>Runs a sync now, for the tray's "Sync now" command.</summary>
    public Task<SyncOutcome> SyncNowAsync() => _coordinator.RunAsync(cancellationToken: _cancellation.Token);

    public Task<SyncOutcome> SyncProjectAsync(ProjectIdentity identity) =>
        _coordinator.RunAsync(new SyncRunOptions { ExactProjectRemote = identity.NormalizedRemote }, _cancellation.Token);

    public Task<bool> AddProjectAsync(string path, string? name, string? remote) =>
        _coordinator.TryUpdateConfigurationAsync(() => ProjectRegistration.Add(path, name, remote), _cancellation.Token);

    public Task<bool> RemoveProjectAsync(ProjectIdentity identity) =>
        _coordinator.TryUpdateConfigurationAsync(() => ProjectRegistration.Remove(identity), _cancellation.Token);

    /// <summary>Changes this PC's automatic sync preference without racing a sync.</summary>
    public Task<bool> SaveAutomaticSyncAsync(bool enabled) =>
        _coordinator.TryUpdateConfigurationAsync(() =>
        {
            var config = LocalConfig.Load();
            config.AutomaticSyncOnProviderClose = enabled;
            config.Save();
        }, _cancellation.Token);

    /// <summary>Updates settings without racing an active sync run.</summary>
    public Task<bool> SaveSettingsAsync(string folder, string? remote, bool initialize) =>
        _coordinator.TryUpdateConfigurationAsync(
            () => SyncRepositorySettings.Save(folder, remote, initialize), _cancellation.Token);

    /// <summary>Save session restore choices only on this PC, without racing a sync.</summary>
    public Task<bool> SaveRestoreSelectionsAsync(
        IReadOnlyList<(ProjectIdentity Project, IReadOnlyList<string>? SessionIds)> selections) =>
        _coordinator.TryUpdateConfigurationAsync(() =>
        {
            var config = LocalConfig.Load();
            foreach (var (project, sessionIds) in selections)
            {
                config.SetRestoreSelection(_provider.Id, project, sessionIds);
            }

            config.Save();
        }, _cancellation.Token);

    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync().ConfigureAwait(false);

        if (_watching is not null)
        {
            try
            {
                await _watching.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Shutting the watch down is expected.
            }
        }

        _cancellation.Dispose();
    }

    private void OnSyncRequested(object? sender, WatchTriggerReason reason)
    {
        try
        {
            if (!LocalConfig.Load().AutomaticSyncOnProviderClose)
            {
                return;
            }
        }
        catch (IOException)
        {
            // Let the coordinator report the invalid configuration to the tray.
        }
        catch (UnauthorizedAccessException)
        {
            // Let the coordinator report the unreadable configuration to the tray.
        }

        _ = _coordinator.RunAsync(cancellationToken: _cancellation.Token);
    }

    /// <summary>
    /// Builds the sync pipeline from the current configuration, adding Git only when
    /// the sync folder can actually be published.
    /// </summary>
    private SyncOrchestrator CreateOrchestrator()
    {
        var localConfig = LocalConfig.Load();

        if (localConfig.SyncRootPath is not { Length: > 0 } syncRoot)
        {
            throw new SyncConfigurationException(
                "No sync folder is configured on this PC. Run 'codechatsync config set-sync-root <path>'.");
        }

        if (!Directory.Exists(syncRoot))
        {
            throw new SyncConfigurationException($"The configured sync folder no longer exists: {syncRoot}");
        }

        var publisher = GitSyncPublisher.TryCreate(syncRoot, out _);

        return new SyncOrchestrator(
            _provider,
            new SyncWorkspace(localConfig, SharedConfig.Load(syncRoot)),
            new ChatSyncService(new ProcessGuard()),
            publisher);
    }
}
