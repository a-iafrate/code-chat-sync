namespace CodeChatSync.Core;

/// <summary>A configuration entry that could not be turned into a usable project.</summary>
public sealed record UnresolvedProject(string Remote, string Reason);

/// <summary>Registered projects plus the entries that had to be skipped.</summary>
public sealed record ProjectResolution
{
    public required IReadOnlyList<ProjectInfo> Projects { get; init; }

    public required IReadOnlyList<UnresolvedProject> Unresolved { get; init; }
}

/// <summary>
/// Combines the per-PC configuration with the sync repository's shared mapping into
/// the projects a sync run operates on, and locates the per-PC baselines.
/// </summary>
public sealed class SyncWorkspace(LocalConfig localConfig, SharedConfig sharedConfig, string? stateDirectory = null)
{
    private readonly LocalConfig _localConfig = localConfig ?? throw new ArgumentNullException(nameof(localConfig));
    private readonly SharedConfig _sharedConfig = sharedConfig ?? throw new ArgumentNullException(nameof(sharedConfig));
    private readonly string _stateDirectory = stateDirectory ?? LocalConfig.GetDefaultStateDirectory();

    /// <summary>Sync folder configured on this PC, if any.</summary>
    public string? SyncRootPath => _localConfig.SyncRootPath;

    /// <summary>
    /// Builds a <see cref="ProjectInfo"/> for every project registered on this PC,
    /// taking the folder name from the shared mapping so all PCs agree on it.
    /// </summary>
    public ProjectResolution ResolveProjects()
    {
        var projects = new List<ProjectInfo>();
        var unresolved = new List<UnresolvedProject>();

        foreach (var entry in _localConfig.Projects)
        {
            if (!ProjectIdentity.TryFromRemote(entry.Remote, out var identity) || identity is null)
            {
                unresolved.Add(new UnresolvedProject(entry.Remote, "The configured remote could not be parsed."));
                continue;
            }

            if (!Directory.Exists(entry.LocalPath))
            {
                unresolved.Add(new UnresolvedProject(
                    identity.NormalizedRemote,
                    $"The local path '{entry.LocalPath}' does not exist on this PC."));
                continue;
            }

            projects.Add(new ProjectInfo
            {
                Identity = identity,
                LocalPath = entry.LocalPath,
                DisplayName = _sharedConfig.Find(identity)?.Name
            });
        }

        return new ProjectResolution { Projects = projects, Unresolved = unresolved };
    }

    /// <summary>Per-PC restore selection, or null to restore every session.</summary>
    public LocalRestoreSelection? GetRestoreSelection(string providerId, ProjectInfo project) =>
        _localConfig.FindRestoreSelection(providerId, project.Identity);

    /// <summary>
    /// Path of the baseline file for one project and provider. Baselines record what
    /// this PC last synced, so they stay local and are never committed.
    /// </summary>
    public string GetStatePath(string providerId, ProjectInfo project)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(project);

        return Path.Combine(_stateDirectory, providerId, $"{project.Identity.Slug}.json");
    }
}
