using CodeChatSync.Core;
using CodeChatSync.Git;
using CodeChatSync.Providers.Claude;
using CodeChatSync.Providers.VisualStudio;

namespace CodeChatSync.App.Services;

/// <summary>One chat changed on both sides since the last sync, needing a choice.</summary>
public sealed record ConflictInfo(string ProviderId, ProjectInfo Project, string RelativePath, string? Reason);

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
    private readonly (string ProviderId, ProviderWatcher Watcher)[] _watchers;
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
            (_visualStudioProvider.Id, new ProviderWatcher(_processGuard, _visualStudioProvider.ProcessNames, watchOptions)),
            (_claudeProvider.Id, new ProviderWatcher(_processGuard, _claudeProvider.ProcessNames, watchOptions))
        ];

        _coordinator.Completed += (_, outcome) => SyncCompleted?.Invoke(this, outcome);
        _coordinator.Started += (_, _) => SyncStarted?.Invoke(this, EventArgs.Empty);
        foreach (var (_, watcher) in _watchers)
        {
            watcher.ProviderRunningChanged += OnProviderRunningChanged;
            watcher.SyncRequested += OnSyncRequested;
        }
    }

    /// <summary>Raised when a sync run finishes, from a background thread.</summary>
    public event EventHandler<SyncOutcome>? SyncCompleted;

    /// <summary>Raised when a real (non-dry-run) sync starts, from a background thread.</summary>
    public event EventHandler? SyncStarted;

    /// <summary>Raised when the set of running, not skipped providers changes; true while any is running.</summary>
    public event EventHandler<bool>? ProviderRunningChanged;

    /// <summary>
    /// Whether a provider that automatic sync must wait for was running at the last check.
    /// Providers whose running check is skipped on this PC do not count.
    /// </summary>
    public bool IsProviderRunning
    {
        get
        {
            var config = TryLoadConfig();
            return _watchers.Any(entry => entry.Watcher.IsProviderRunning
                && config?.IsRunningCheckSkipped(entry.ProviderId) != true);
        }
    }

    /// <summary>Whether a sync is in progress.</summary>
    public bool IsSyncing => _coordinator.IsRunning;

    /// <summary>Outcome of the most recent sync, if any has run yet.</summary>
    public SyncOutcome? LastOutcome => _coordinator.LastOutcome;

    /// <summary>The most recent finished runs, newest first, for the window's log.</summary>
    public IReadOnlyList<SyncLogEntry> RecentRuns => _coordinator.RecentRuns;

    /// <summary>Starts watching for either provider closing.</summary>
    public void Start() => _watching ??= _watchers
        .Select(entry => entry.Watcher.WatchAsync(_cancellation.Token))
        .ToArray();

    /// <summary>Runs a sync now, for the tray's "Sync now" command.</summary>
    public Task<SyncOutcome> SyncNowAsync() =>
        _coordinator.RunOrWaitAsync(cancellationToken: _cancellation.Token);

    /// <summary>
    /// Everything archived in the sync folder for the projects registered on this PC, for
    /// the Chats page. Reads the archive only — never a tool's live storage — so it is safe
    /// to run while Visual Studio or Claude Code is open, and takes no part in the sync gate.
    /// </summary>
    public Task<IReadOnlyList<ChatLibraryProject>> ListArchivedChatsAsync() =>
        Task.Run<IReadOnlyList<ChatLibraryProject>>(() =>
        {
            var config = LocalConfig.Load();
            if (config.SyncRootPath is not { Length: > 0 } syncRoot || !Directory.Exists(syncRoot))
            {
                return [];
            }

            return ChatLibrary.List(config, SharedConfig.Load(syncRoot), syncRoot, [_visualStudioProvider, _claudeProvider]);
        }, _cancellation.Token);

    /// <summary>
    /// One archived conversation, for the Chats page to show, or <see langword="null"/> when
    /// it is not in the archive. Reads the archive only, like <see cref="ListArchivedChatsAsync"/>.
    /// </summary>
    public Task<ArchivedChatContent?> ReadArchivedChatAsync(string providerId, ProjectIdentity project, string chatId) =>
        Task.Run<ArchivedChatContent?>(() =>
        {
            var config = LocalConfig.Load();
            if (config.SyncRootPath is not { Length: > 0 } syncRoot || !Directory.Exists(syncRoot))
            {
                return null;
            }

            return ChatLibrary.Read(
                config, SharedConfig.Load(syncRoot), syncRoot, [_visualStudioProvider, _claudeProvider],
                providerId, project, chatId);
        }, _cancellation.Token);

    public Task<SyncOutcome> SyncProjectAsync(ProjectIdentity identity) =>
        _coordinator.RunOrWaitAsync(
            new SyncRunOptions { ExactProjectRemote = identity.NormalizedRemote }, cancellationToken: _cancellation.Token);

    /// <summary>
    /// Chats currently changed on both sides across every registered project: the same
    /// comparison a sync performs, without writing anything, surfaced for the user to
    /// resolve one by one. A pull from the sync repository is skipped for this check the
    /// same way <c>--dry-run</c> skips it, so this reflects the current local clone rather
    /// than fetching other PCs' latest pushes first.
    /// </summary>
    public async Task<(IReadOnlyList<ConflictInfo> Conflicts, string? Message)> GetConflictsAsync()
    {
        // This dry run fires automatically and often — on startup, after every sync, after
        // resolving a conflict — so it routinely races the user's own "Sync now" click or
        // another automatic refresh for the same brief gate. RunOrWaitAsync resolves that
        // silently in the common case; only a gate still busy after the retry window is
        // worth telling the user about, since by then something really is taking a while.
        var outcome = await _coordinator.RunOrWaitAsync(
            new SyncRunOptions { DryRun = true }, cancellationToken: _cancellation.Token).ConfigureAwait(false);
        if (outcome.Status is SyncOutcomeStatus.AlreadyRunning)
        {
            return ([], "A sync is already running. Try again once it finishes.");
        }

        if (outcome.Result is not { } result)
        {
            return ([], outcome.Summary);
        }

        var conflicts = result.Projects
            .SelectMany(project => project.Report.Entries
                .Where(entry => entry.Action == SyncAction.Conflict)
                .Select(entry => new ConflictInfo(project.ProviderId, project.Project, entry.RelativePath, entry.Reason)))
            .ToArray();

        return (conflicts, null);
    }

    /// <summary>
    /// Resolves one conflict by forcing the chosen side to win: "keep local" pushes this
    /// PC's version over the sync folder's copy, otherwise the sync folder's version is
    /// pulled onto this PC and the replaced local file is backed up first.
    /// </summary>
    public Task<bool> ResolveConflictAsync(ConflictInfo conflict, bool keepLocal) =>
        _coordinator.TryUpdateConfigurationAsync(() =>
        {
            var localConfig = LocalConfig.Load();
            if (localConfig.SyncRootPath is not { Length: > 0 } syncRoot)
            {
                throw new InvalidOperationException("No sync folder is configured on this PC.");
            }

            var guard = ProcessGuard.ForSync(_processGuard, localConfig, [_visualStudioProvider, _claudeProvider]);
            var provider = ResolveProvider(conflict.ProviderId, guard);
            var statePath = new SyncWorkspace(localConfig, SharedConfig.Load(syncRoot)).GetStatePath(conflict.ProviderId, conflict.Project);
            var state = SyncState.Load(statePath);

            var result = new ChatSyncService(guard)
                .ResolveConflict(provider, conflict.Project, syncRoot, state, conflict.RelativePath, keepLocal);
            state.Save(statePath);

            if (result.Action is SyncAction.Skipped)
            {
                throw new InvalidOperationException(result.Reason ?? "The conflict could not be resolved.");
            }
        }, _cancellation.Token);

    private IChatProvider ResolveProvider(string providerId, IProcessGuard guard)
    {
        if (string.Equals(providerId, _visualStudioProvider.Id, StringComparison.OrdinalIgnoreCase))
        {
            return _visualStudioProvider;
        }

        if (string.Equals(providerId, _claudeProvider.Id, StringComparison.OrdinalIgnoreCase))
        {
            // Rebuilt with this call's own guard, same as every other sync-triggering path.
            return new ClaudeCodeChatProvider(guard);
        }

        throw new ArgumentException($"Unknown provider '{providerId}'.", nameof(providerId));
    }

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

    /// <summary>
    /// Registers a Claude Code project from its repository root, which is what the list
    /// offers: its sessions may have been started in any of its subfolders, each stored by
    /// Claude Code under a folder of its own.
    /// </summary>
    public Task<bool> AddClaudeProjectAsync(string path) =>
        _coordinator.TryUpdateConfigurationAsync(() =>
        {
            // Checked again here rather than trusted from the list, which may be stale.
            var usable = ClaudeProjectDiscovery.DiscoverCandidates(CreateDiscoveryProcessGuard())
                .Where(item => ClaudeProjectDiscovery.IsWithinProject(item.LocalPath, path))
                .Where(item => item.LocalPathExists && !item.HasStorageCollision
                    && item.StorageFolderName is not null
                    && item.Sessions.Any(session => session.IsInStorageFolder))
                .ToArray();

            if (usable.Length == 0)
            {
                throw new InvalidOperationException(
                    "This project has no safely mapped Claude Code transcripts on this PC. Refresh the list.");
            }

            var root = GitRemoteReader.FindRepositoryRoot(path);
            var remote = root is null ? null : GitRemoteReader.FindPrimaryRemoteUrl(root);
            if (root is null || !string.Equals(root, path, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(remote))
            {
                throw new InvalidOperationException("The Claude Code project must be a Git repository root with a remote.");
            }

            var existing = LocalConfig.Load().Find(ProjectIdentity.FromRemote(remote));
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
    public async Task<bool> SaveSkipRunningCheckAsync(string providerId, bool skip)
    {
        if (!string.Equals(providerId, _visualStudioProvider.Id, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(providerId, _claudeProvider.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Unknown provider '{providerId}'.", nameof(providerId));
        }

        var saved = await _coordinator.TryUpdateConfigurationAsync(() =>
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

        if (saved)
        {
            OnProviderRunningChanged(this, skip);
        }

        return saved;
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
        // Providers skipped on this PC do not hold back the sync triggered by another one closing.
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
    /// Reads the per-PC config for status decisions; an unreadable config skips nothing,
    /// so automatic sync keeps waiting for every tool.
    /// </summary>
    private static LocalConfig? TryLoadConfig()
    {
        try
        {
            return LocalConfig.Load();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
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
