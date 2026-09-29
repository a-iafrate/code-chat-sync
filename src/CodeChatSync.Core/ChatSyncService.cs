namespace CodeChatSync.Core;

/// <summary>
/// Copies chat files between this PC and the sync folder. Never links files, always
/// backs up a local file before replacing it, and never writes local chat data while
/// the provider's tool is running.
/// </summary>
public sealed class ChatSyncService(IProcessGuard processGuard)
{
    private readonly IProcessGuard _processGuard = processGuard
        ?? throw new ArgumentNullException(nameof(processGuard));

    /// <summary>Directory holding backups of replaced local files.</summary>
    public static string GetBackupDirectory(string syncRootPath) =>
        Path.Combine(Path.GetFullPath(syncRootPath), ".backups");

    /// <summary>
    /// Synchronizes one project in both directions.
    /// </summary>
    /// <param name="dryRun">
    /// When true, reports the actions that would be taken without copying anything.
    /// </param>
    public SyncReport Sync(
        IChatProvider provider,
        ProjectInfo project,
        string syncRootPath,
        SyncState state,
        bool dryRun = false)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(syncRootPath);

        var running = _processGuard.GetRunningProcesses(provider.ProcessNames);
        var projectSyncRoot = Path.Combine(
            Path.GetFullPath(syncRootPath),
            provider.Id,
            project.SyncFolderName);

        var (relativePaths, inUsePaths) = CollectRelativePaths(provider, project, projectSyncRoot);
        var results = new List<SyncEntryResult>(relativePaths.Count);

        foreach (var relativePath in relativePaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (inUsePaths.Contains(relativePath))
            {
                // The provider reports this chat as open: copying either way risks a torn file.
                results.Add(new SyncEntryResult
                {
                    RelativePath = relativePath,
                    Action = SyncAction.Skipped,
                    Reason = "The chat is currently open."
                });
                continue;
            }

            results.Add(SyncSingle(provider, project, projectSyncRoot, syncRootPath, state, relativePath, running, dryRun));
        }

        return new SyncReport { Entries = results };
    }

    private static (HashSet<string> Paths, HashSet<string> InUsePaths) CollectRelativePaths(
        IChatProvider provider,
        ProjectInfo project,
        string projectSyncRoot)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inUsePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var location in provider.Discover(project))
        {
            var relativePath = RelativePathGuard.Normalize(location.RelativePath);
            paths.Add(relativePath);

            if (location.IsInUse)
            {
                inUsePaths.Add(relativePath);
            }
        }

        if (Directory.Exists(projectSyncRoot))
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = true
            };

            foreach (var file in Directory.EnumerateFiles(projectSyncRoot, "*", options))
            {
                paths.Add(RelativePathGuard.Normalize(Path.GetRelativePath(projectSyncRoot, file)));
            }
        }

        return (paths, inUsePaths);
    }

    private SyncEntryResult SyncSingle(
        IChatProvider provider,
        ProjectInfo project,
        string projectSyncRoot,
        string syncRootPath,
        SyncState state,
        string relativePath,
        IReadOnlyList<string> runningProcesses,
        bool dryRun)
    {
        var localPath = provider.MapToLocal(project, relativePath);
        var syncPath = RelativePathGuard.ResolveUnder(projectSyncRoot, relativePath);

        var localHash = SyncState.ComputeHash(localPath);
        var syncHash = SyncState.ComputeHash(syncPath);
        var baseline = state.GetBaseline(relativePath);

        if (localHash is null && syncHash is null)
        {
            state.Remove(relativePath);
            return new SyncEntryResult { RelativePath = relativePath, Action = SyncAction.Skipped, Reason = "File missing on both sides." };
        }

        if (localHash is not null && localHash == syncHash)
        {
            if (!dryRun)
            {
                state.SetBaseline(relativePath, localHash);
            }

            return new SyncEntryResult { RelativePath = relativePath, Action = SyncAction.Unchanged };
        }

        var localChanged = localHash != baseline;
        var syncChanged = syncHash != baseline;

        if (localChanged && syncChanged)
        {
            return new SyncEntryResult
            {
                RelativePath = relativePath,
                Action = SyncAction.Conflict,
                Reason = "Changed on this PC and in the sync folder since the last sync."
            };
        }

        return localChanged
            ? Push(relativePath, localPath, syncPath, localHash, state, dryRun)
            : Pull(relativePath, localPath, syncPath, syncHash, syncRootPath, state, runningProcesses, dryRun);
    }

    private static SyncEntryResult Push(
        string relativePath,
        string localPath,
        string syncPath,
        string? localHash,
        SyncState state,
        bool dryRun)
    {
        if (localHash is null)
        {
            // Deleting from the sync folder is not part of this phase: report instead of guessing.
            return new SyncEntryResult
            {
                RelativePath = relativePath,
                Action = SyncAction.Skipped,
                Reason = "Removed on this PC; deletions are not propagated."
            };
        }

        if (!dryRun)
        {
            var directory = Path.GetDirectoryName(syncPath);
            if (directory is { Length: > 0 })
            {
                Directory.CreateDirectory(directory);
            }

            File.Copy(localPath, syncPath, overwrite: true);
            state.SetBaseline(relativePath, localHash);
        }

        return new SyncEntryResult { RelativePath = relativePath, Action = SyncAction.Pushed };
    }

    private SyncEntryResult Pull(
        string relativePath,
        string localPath,
        string syncPath,
        string? syncHash,
        string syncRootPath,
        SyncState state,
        IReadOnlyList<string> runningProcesses,
        bool dryRun)
    {
        if (syncHash is null)
        {
            return new SyncEntryResult
            {
                RelativePath = relativePath,
                Action = SyncAction.Skipped,
                Reason = "Removed in the sync folder; deletions are not propagated."
            };
        }

        // Writing local chat data while the tool is open risks corrupting files it
        // holds. Only the pull is held back: pushes are unaffected, so the rest of the
        // sync still makes progress and this file completes once the tool is closed.
        if (runningProcesses.Count > 0)
        {
            return new SyncEntryResult
            {
                RelativePath = relativePath,
                Action = SyncAction.Skipped,
                Reason = $"Waiting for {string.Join(", ", runningProcesses)} to close before writing local chat data.",
                IsBlockedByProvider = true
            };
        }

        string? backupPath = null;
        if (!dryRun)
        {
            if (File.Exists(localPath))
            {
                backupPath = CreateBackup(localPath, relativePath, syncRootPath);
            }

            var directory = Path.GetDirectoryName(localPath);
            if (directory is { Length: > 0 })
            {
                Directory.CreateDirectory(directory);
            }

            File.Copy(syncPath, localPath, overwrite: true);
            state.SetBaseline(relativePath, syncHash);
        }

        return new SyncEntryResult { RelativePath = relativePath, Action = SyncAction.Pulled, BackupPath = backupPath };
    }

    private static string CreateBackup(string localPath, string relativePath, string syncRootPath)
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        var backupRoot = Path.Combine(GetBackupDirectory(syncRootPath), timestamp);
        var backupPath = RelativePathGuard.ResolveUnder(backupRoot, relativePath);

        var directory = Path.GetDirectoryName(backupPath);
        if (directory is { Length: > 0 })
        {
            Directory.CreateDirectory(directory);
        }

        File.Copy(localPath, backupPath, overwrite: false);
        return backupPath;
    }
}
