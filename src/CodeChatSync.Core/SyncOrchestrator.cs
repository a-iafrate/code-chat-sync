namespace CodeChatSync.Core;

/// <summary>Outcome of one step of publishing to the sync repository.</summary>
public sealed record SyncPublishResult(bool CanContinue, string? Message)
{
    public static SyncPublishResult Ok(string? message = null) => new(true, message);

    public static SyncPublishResult Stop(string message) => new(false, message);
}

/// <summary>
/// Publishes the sync folder's contents so other PCs can see them.
/// </summary>
/// <remarks>
/// Declared here so the sync flow stays independent of Git: the implementation
/// lives in <c>CodeChatSync.Git</c> and is composed in by the CLI and the app.
/// </remarks>
public interface ISyncPublisher
{
    /// <summary>
    /// Brings in other PCs' changes before local files are compared. Returning
    /// <see cref="SyncPublishResult.CanContinue"/> as <see langword="false"/> aborts
    /// the run without touching any file.
    /// </summary>
    SyncPublishResult PrepareForSync();

    /// <summary>Publishes what this run copied into the sync folder.</summary>
    SyncPublishResult PublishChanges(int pushedCount);
}

/// <summary>What a single sync run should do.</summary>
public sealed record SyncRunOptions
{
    /// <summary>Report what would happen without copying or publishing anything.</summary>
    public bool DryRun { get; init; }

    /// <summary>Sync only the project matching this remote, slug or folder name.</summary>
    public string? ProjectFilter { get; init; }
}

/// <summary>Result of syncing one project.</summary>
public sealed record ProjectSyncResult(ProjectInfo Project, SyncReport Report);

/// <summary>Everything a caller needs to report a sync run.</summary>
public sealed record SyncRunResult
{
    public required IReadOnlyList<ProjectSyncResult> Projects { get; init; }

    public required IReadOnlyList<UnresolvedProject> Unresolved { get; init; }

    /// <summary>Set when the run stopped before any file was touched.</summary>
    public string? AbortReason { get; init; }

    /// <summary>Message from the pre-sync step, when there is something to report.</summary>
    public string? PrepareMessage { get; init; }

    /// <summary>Message from the publish step, when there is something to report.</summary>
    public string? PublishMessage { get; init; }

    public bool Aborted => AbortReason is not null;

    public int PushedCount => Projects.Sum(project => project.Report.PushedCount);

    public int PulledCount => Projects.Sum(project => project.Report.PulledCount);

    public int ConflictCount => Projects.Sum(project => project.Report.ConflictCount);

    public bool HasConflicts => ConflictCount > 0;

    public bool HasBlockedPulls => Projects.Any(project => project.Report.HasBlockedPulls);
}

/// <summary>Raised when a sync run cannot start because of how the PC is configured.</summary>
public sealed class SyncConfigurationException(string message) : Exception(message);

/// <summary>
/// Runs a complete sync: publish preparation, per-project copying, baseline
/// persistence, then publishing. Produces a report instead of writing anywhere, so
/// the CLI and the tray app share exactly the same behaviour.
/// </summary>
public sealed class SyncOrchestrator(
    IChatProvider provider,
    SyncWorkspace workspace,
    ChatSyncService syncService,
    ISyncPublisher? publisher = null)
{
    private readonly IChatProvider _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    private readonly SyncWorkspace _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
    private readonly ChatSyncService _syncService = syncService ?? throw new ArgumentNullException(nameof(syncService));

    public SyncRunResult Run(SyncRunOptions? options = null)
    {
        var runOptions = options ?? new SyncRunOptions();

        if (_workspace.SyncRootPath is not { Length: > 0 } syncRoot)
        {
            throw new SyncConfigurationException("No sync folder is configured on this PC.");
        }

        if (!Directory.Exists(syncRoot))
        {
            throw new SyncConfigurationException($"The configured sync folder no longer exists: {syncRoot}");
        }

        var resolution = _workspace.ResolveProjects();
        var projects = Filter(resolution.Projects, runOptions.ProjectFilter);

        if (projects.Count == 0)
        {
            return new SyncRunResult { Projects = [], Unresolved = resolution.Unresolved };
        }

        string? prepareMessage = null;
        if (!runOptions.DryRun && publisher is not null)
        {
            var prepared = publisher.PrepareForSync();
            prepareMessage = prepared.Message;

            if (!prepared.CanContinue)
            {
                return new SyncRunResult
                {
                    Projects = [],
                    Unresolved = resolution.Unresolved,
                    AbortReason = prepared.Message ?? "The sync folder could not be prepared.",
                    PrepareMessage = prepareMessage
                };
            }
        }

        var results = new List<ProjectSyncResult>();
        foreach (var project in projects)
        {
            var statePath = _workspace.GetStatePath(_provider.Id, project);
            var state = SyncState.Load(statePath);
            Func<string, bool>? shouldRestore = null;
            if (_workspace.GetRestoreSelection(_provider.Id, project) is { } selection)
            {
                if (_provider is not IChatSessionProvider sessionProvider)
                {
                    throw new SyncConfigurationException($"Provider '{_provider.Id}' does not support session-level restore selection.");
                }

                if (selection.SessionIds is null)
                {
                    throw new SyncConfigurationException("The local restore selection is missing its session IDs.");
                }

                var selectedIds = new HashSet<string>(selection.SessionIds, StringComparer.OrdinalIgnoreCase);
                shouldRestore = path => selectedIds.Contains(sessionProvider.GetSessionId(path));
            }

            var report = _syncService.Sync(_provider, project, syncRoot, state, runOptions.DryRun, shouldRestore);

            if (!runOptions.DryRun)
            {
                state.Save(statePath);
            }

            results.Add(new ProjectSyncResult(project, report));
        }

        string? publishMessage = null;
        if (!runOptions.DryRun && publisher is not null)
        {
            publishMessage = publisher.PublishChanges(results.Sum(result => result.Report.PushedCount)).Message;
        }

        return new SyncRunResult
        {
            Projects = results,
            Unresolved = resolution.Unresolved,
            PrepareMessage = prepareMessage,
            PublishMessage = publishMessage
        };
    }

    private static IReadOnlyList<ProjectInfo> Filter(IReadOnlyList<ProjectInfo> projects, string? filter)
    {
        if (filter is not { Length: > 0 })
        {
            return projects;
        }

        return [.. projects.Where(project => Matches(project, filter))];
    }

    private static bool Matches(ProjectInfo project, string filter) =>
        project.Identity.NormalizedRemote.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || project.SyncFolderName.Equals(filter, StringComparison.OrdinalIgnoreCase)
        || project.Identity.Slug.Equals(filter, StringComparison.OrdinalIgnoreCase);
}
