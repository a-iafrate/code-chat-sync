using CodeChatSync.Core;

namespace CodeChatSync.Git;

/// <summary>Registers local project paths by Git remote without touching client repositories.</summary>
public static class ProjectRegistration
{
    public static ProjectInfo Add(
        string path,
        string? name = null,
        string? remoteOverride = null,
        string? configPath = null,
        string providerId = LocalConfig.DefaultProviderId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path.Trim());
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"Project folder not found: {fullPath}");
        }

        var local = LocalConfig.Load(configPath);
        if (local.SyncRootPath is not { Length: > 0 } syncRoot || !Directory.Exists(syncRoot))
        {
            throw new InvalidOperationException("Configure an existing private sync folder before adding a project.");
        }

        var repositoryRoot = GitRemoteReader.FindRepositoryRoot(fullPath);
        if (repositoryRoot is null && string.IsNullOrWhiteSpace(remoteOverride))
        {
            throw new InvalidOperationException("No Git repository was found. Choose a repository or provide its remote URL explicitly.");
        }

        var registeredPath = repositoryRoot ?? fullPath;
        if (Overlaps(registeredPath, syncRoot))
        {
            throw new ArgumentException("The project folder must not overlap the private sync folder.", nameof(path));
        }

        var remote = string.IsNullOrWhiteSpace(remoteOverride)
            ? GitRemoteReader.FindPrimaryRemoteUrl(registeredPath)
            : remoteOverride.Trim();
        if (string.IsNullOrWhiteSpace(remote))
        {
            throw new InvalidOperationException("No unambiguous Git remote was found. Add an origin or provide a remote URL explicitly.");
        }

        GitRemoteUrlGuard.ThrowIfContainsPassword(remote, nameof(remoteOverride));

        var identity = ProjectIdentity.FromRemote(remote);
        var shared = SharedConfig.Load(syncRoot);
        var existing = shared.Find(identity);
        var folderName = shared.AddOrUpdate(identity, name).Name;
        if (shared.Projects.Any(entry => !string.Equals(entry.Remote, identity.NormalizedRemote, StringComparison.OrdinalIgnoreCase)
            && string.Equals(entry.Name, folderName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"The sync folder name '{folderName}' is already used by another project.", nameof(name));
        }

        // Never rename a folder already used for synced chats implicitly.
        if (existing is not null && !string.Equals(existing.Name, folderName, StringComparison.OrdinalIgnoreCase)
            && new[] { "visualstudio", "claudecode" }.Any(providerId =>
                Directory.Exists(Path.Combine(syncRoot, providerId, existing.Name))))
        {
            throw new InvalidOperationException("The project already has archived chats. Keep its existing sync folder name.");
        }

        local.AddOrUpdate(identity, registeredPath, providerId);
        shared.Save(syncRoot);
        local.Save(configPath);
        return new ProjectInfo { Identity = identity, LocalPath = registeredPath, DisplayName = folderName };
    }

    /// <summary>Removes the local registration only; archived chats and shared mapping remain.</summary>
    public static void Remove(ProjectIdentity identity, string? configPath = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var local = LocalConfig.Load(configPath);
        var entry = local.Find(identity)
            ?? throw new InvalidOperationException($"Project not registered on this PC: {identity.NormalizedRemote}");
        local.Projects.Remove(entry);
        local.Save(configPath);
    }

    private static bool Overlaps(string projectPath, string syncPath)
    {
        var project = Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var sync = Path.GetFullPath(syncPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return sync.StartsWith(project, StringComparison.OrdinalIgnoreCase)
            || project.StartsWith(sync, StringComparison.OrdinalIgnoreCase);
    }
}
