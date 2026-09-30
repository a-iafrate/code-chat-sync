using CodeChatSync.Core;
using CodeChatSync.Git;
using CodeChatSync.Providers.Claude;
using CodeChatSync.Providers.VisualStudio;

namespace CodeChatSync.App.Services;

/// <summary>
/// Composes both chat providers for the tray app, watches their processes, and reports sync outcomes.
/// </summary>
/// <remarks>
/// Configuration is re-read for every run, so changing the sync folder or
/// registering a project does not require restarting the app.
/// </remarks>
public sealed class SyncHost : IAsyncDisposable
{
    private readonly IProcessGuard _processGuard;
    private readonly VisualStudioChatProvider _visualStudioProvider;
    private readonly ClaudeCodeChatProvider _claudeProvider;
    private readonly SyncCoordinator _coordinator;
    private readonly ProviderWatcher[] _watchers;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly object _providerRunningLock = new();

    private Task[]? _watching;
    private bool _reportedProviderRunning;

    public SyncHost(WatchOptions? watchOptions = null)
    {
        _processGuard = new ProcessGuard();
        _visualStudioProvider = new VisualStudioChatProvider();
        _claudeProvider = new ClaudeCodeChatProvider(_processGuard);
        _coordinator = new SyncCoordinator(CreateOrchestrator);
        _watchers =
        [
            new ProviderWatcher(_processGuard, _visualStudioProvider.ProcessNames, watchOptions),
            new ProviderWatcher(_processGuard, _claudeProvider.ProcessNames, watchOptions)
        ];

        _coordinator.Completed += (_, outcome) => SyncCompleted?.Invoke(this, outcome);
        foreach (var watcher in _watchers)
        {
            watcher.ProviderRunningChanged += OnProviderRunningChanged;
            watcher.SyncRequested += OnSyncRequested;
        }
    }

    /// <summary>Raised when a sync run finishes, from a background thread.</summary>
    public event EventHandler<SyncOutcome>? SyncCompleted;

    /// <summary>Raised when either provider starts or stops; true while any provider is running.</summary>
    public event EventHandler<bool>? ProviderRunningChanged;

    /// <summary>Whether any registered chat provider was running at the last check.</summary>
    public bool IsProviderRunning => _watchers.Any(watcher => watcher.IsProviderRunning);

    /// <summary>Whether a sync is in progress.</summary>
    public bool IsSyncing => _coordinator.IsRunning;

    /// <summary>Outcome of the most recent sync, if any has run yet.</summary>
    public SyncOutcome? LastOutcome => _coordinator.LastOutcome;

    /// <summary>Starts watching for either provider closing.</summary>
    public void Start() => _watching ??= _watchers
        .Select(watcher => watcher.WatchAsync(_cancellation.Token))
        .ToArray();

    /// <summary>Runs a sync now, for the tray's "Sync now" command.</summary>
    public Task<SyncOutcome> SyncNowAsync() => _coordinator.RunAsync(cancellationToken: _cancellation.Token);

    public Task<SyncOutcome> SyncProjectAsync(ProjectIdentity identity) =>
        _coordinator.RunAsync(new SyncRunOptions { ExactProjectRemote = identity.NormalizedRemote }, _cancellation.Token);

    public Task<bool> AddProjectAsync(
        string path,
        string? name,
        string? remote,
        string providerId = LocalConfig.DefaultProviderId) =>
        _coordinator.TryUpdateConfigurationAsync(
            () => ProjectRegistration.Add(path, name, remote, providerId: providerId),
            _cancellation.Token);

    public Task<bool> RemoveProjectAsync(ProjectIdentity identity) =>
        _coordinator.TryUpdateConfigurationAsync(() => ProjectRegistration.Remove(identity), _cancellation.Token);

    public Task<bool> AddClaudeProjectAsync(string path) =>
        _coordinator.TryUpdateConfigurationAsync(() =>
        {
            var candidate = ClaudeProjectDiscovery.DiscoverCandidates(CreateDiscoveryProcessGuard())
                .FirstOrDefault(item => string.Equals(item.LocalPath, path, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("The selected Claude Code project is no longer available. Refresh the list.");
            if (!candidate.LocalPathExists || candidate.HasStorageCollision
                || candidate.StorageFolderName is null || !candidate.Sessions.Any(session => session.IsInStorageFolder))
            {
                throw new InvalidOperationException("The Claude Code project has no safely mapped transcripts on this PC.");
            }

            var root = GitRemoteReader.FindRepositoryRoot(candidate.LocalPath);
            if (root is null || !string.Equals(root, candidate.LocalPath, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(GitRemoteReader.FindPrimaryRemoteUrl(root)))
            {
                throw new InvalidOperationException("The Claude Code project must match a Git repository root with a remote.");
            }

            var existing = LocalConfig.Load().Find(ProjectIdentity.FromRemote(GitRemoteReader.FindPrimaryRemoteUrl(root)!));
            if (existing is not null && !string.Equals(existing.LocalPath, root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("This Git remote is already registered at a different local folder on this PC.");
            }

            ProjectRegistration.Add(root, providerId: _claudeProvider.Id);
        }, _cancellation.Token);

    /// <summary>Changes this PC's automatic sync preference without racing a sync.</summary>
    public Task<bool> SaveAutomaticSyncAsync(bool enabled) =>
        _coordinator.TryUpdateConfigurationAsync(() =>
        {
            var config = LocalConfig.Load();
            config.AutomaticSyncOnProviderClose = enabled;
            config.Save();
        }, _cancellation.Token);

    /// <summary>Saves this PC's window appearance without racing a sync.</summary>
    public Task<bool> SaveThemePreferenceAsync(ThemePreference preference) =>
        _coordinator.TryUpdateConfigurationAsync(() =>
        {
            var config = LocalConfig.Load();
            config.ThemePreference = preference;
            config.Save();
        }, _cancellation.Token);

    /// <summary>Records first-run guidance choices on this PC; null leaves a flag unchanged.</summary>
    public Task<bool> SaveOnboardingStateAsync(bool? wizardSeen = null, bool? checklistDismissed = null) =>
        _coordinator.TryUpdateConfigurationAsync(() =>
        {
            var config = LocalConfig.Load();
            config.OnboardingWizardSeen = wizardSeen ?? config.OnboardingWizardSeen;
            config.GettingStartedDismissed = checklistDismissed ?? config.GettingStartedDismissed;
            config.Save();
        }, _cancellation.Token);

    /// <summary>Records whether a provider's running check is skipped on this PC.</summary>
    public Task<bool> SaveSkipRunningCheckAsync(string providerId, bool skip)
    {
        if (!string.Equals(providerId, _visualStudioProvider.Id, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(providerId, _claudeProvider.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Unknown provider '{providerId}'.", nameof(providerId));
        }

        return _coordinator.TryUpdateConfigurationAsync(() =>
        {
            var config = LocalConfig.Load();
            config.SkipRunningCheckProviderIds.RemoveAll(id =>
                string.Equals(id, providerId, StringComparison.OrdinalIgnoreCase));
            if (skip)
            {
                config.SkipRunningCheckProviderIds.Add(providerId);
            }

            config.Save();
        }, _cancellation.Token);
    }

    /// <summary>Process guard honoring this PC's current skip settings, for Claude project discovery.</summary>
    public IProcessGuard CreateDiscoveryProcessGuard() =>
        ProcessGuard.ForSync(_processGuard, LocalConfig.Load(), [_claudeProvider]);

    /// <summary>Updates settings without racing an active sync run.</summary>
    public Task<bool> SaveSettingsAsync(string folder, string? remote, bool initialize) =>
        _coordinator.TryUpdateConfigurationAsync(
            () => SyncRepositorySettings.Save(folder, remote, initialize), _cancellation.Token);

    /// <summary>Save session restore choices only on this PC, without racing a sync.</summary>
    public Task<bool> SaveRestoreSelectionsAsync(
        IReadOnlyList<(string ProviderId, ProjectIdentity Project, IReadOnlyList<string>? SessionIds)> selections) =>
        _coordinator.TryUpdateConfigurationAsync(() =>
        {
            var config = LocalConfig.Load();
            foreach (var (providerId, project, sessionIds) in selections)
            {
                var entry = config.Find(project)
                    ?? throw new InvalidOperationException($"Project not registered on this PC: {project.NormalizedRemote}");
                if (!LocalConfig.GetEnabledProviderIds(entry).Contains(providerId, StringComparer.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Provider '{providerId}' is not enabled for {project.NormalizedRemote}.");
                }

                config.SetRestoreSelection(providerId, project, sessionIds);
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
                await Task.WhenAll(_watching).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Shutting the watch down is expected.
            }
        }

        _cancellation.Dispose();
    }

    private void OnProviderRunningChanged(object? sender, bool running)
    {
        lock (_providerRunningLock)
        {
            var isRunning = IsProviderRunning;
            if (isRunning == _reportedProviderRunning)
            {
                return;
            }

            _reportedProviderRunning = isRunning;
            ProviderRunningChanged?.Invoke(this, isRunning);
        }
    }

    private void OnSyncRequested(object? sender, WatchTriggerReason reason)
    {
        if (IsProviderRunning)
        {
            return;
        }

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
        var syncGuard = ProcessGuard.ForSync(_processGuard, localConfig, [_visualStudioProvider, _claudeProvider]);

        // The Claude provider checks the guard itself, so it is rebuilt with this run's guard.
        return new SyncOrchestrator(
            [_visualStudioProvider, new ClaudeCodeChatProvider(syncGuard)],
            new SyncWorkspace(localConfig, SharedConfig.Load(syncRoot)),
            new ChatSyncService(syncGuard),
            publisher);
    }
}
