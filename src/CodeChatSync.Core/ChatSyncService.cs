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
        bool dryRun = false,
        Func<string, bool>? shouldRestore = null)
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

            var restoreAllowed = shouldRestore?.Invoke(relativePath) ?? true;
            results.Add(SyncSingle(provider, project, projectSyncRoot, syncRootPath, state, relativePath, running, dryRun, restoreAllowed));
        }

        return new SyncReport
        {
            Entries = results,
            Registration = RegisterSessions(provider, project, relativePaths, shouldRestore, running, dryRun)
        };
    }

    /// <summary>
    /// Announces the project's restorable sessions to a tool that keeps its own index
    /// of chats, so a chat whose files were copied earlier also becomes visible.
    /// </summary>
    /// <remarks>
    /// This runs on every sync, not only when something was copied: a session restored
    /// before the tool learned to register it would otherwise stay invisible forever,
    /// since its files already match and no copy is needed. The provider is expected to
    /// skip sessions it has already registered.
    /// </remarks>
    private static SessionRegistrationResult? RegisterSessions(
        IChatProvider provider,
        ProjectInfo project,
        IReadOnlyCollection<string> relativePaths,
        Func<string, bool>? shouldRestore,
        IReadOnlyList<string> runningProcesses,
        bool dryRun)
    {
        if (provider is not IChatSessionRegistrar registrar)
        {
            return null;
        }

        // The index belongs to the tool: writing it while the tool is open is the same
        // hazard as writing its chat files.
        if (runningProcesses.Count > 0)
        {
            return new SessionRegistrationResult
            {
                RegisteredCount = 0,
                Reason = $"Waiting for {string.Join(", ", runningProcesses)} to close before updating its chat list.",
                IsBlockedByProvider = true
            };
        }

        var sessionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relativePath in relativePaths)
        {
            if (!(shouldRestore?.Invoke(relativePath) ?? true))
            {
                continue;
            }

            try
            {
                sessionIds.Add(registrar.GetSessionId(relativePath));
            }
            catch (ArgumentException)
            {
                // A file that does not sit under a session folder: nothing to register.
            }
        }

        return sessionIds.Count == 0 ? null : registrar.RegisterSessions(project, sessionIds, dryRun);
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
        bool dryRun,
        bool restoreAllowed)
    {
        var localPath = provider.MapToLocal(project, relativePath);
        var syncPath = RelativePathGuard.ResolveUnder(projectSyncRoot, relativePath);

        if (!restoreAllowed && !File.Exists(localPath) && File.Exists(syncPath))
        {
            return new SyncEntryResult
            {
                RelativePath = relativePath,
                Action = SyncAction.Skipped,
                Reason = "This session is not selected for restore on this PC."
            };
        }

        // A file the provider derived here from already-synced data is treated as absent:
        // it is nothing to preserve, so an incoming copy replaces it instead of colliding
        // with it, and it is never published back.
        var derivedLocally = provider is IDerivedChatContent derived
            && derived.IsDerivedLocally(project, relativePath);

        var mapper = provider is IChatContentMapper candidate && candidate.IsMapped(relativePath) ? candidate : null;
        var portableLocal = !derivedLocally && mapper is not null && File.Exists(localPath)
            ? mapper.ToPortable(project, File.ReadAllBytes(localPath))
            : null;
        var localHash = derivedLocally
            ? null
            : portableLocal is not null
                ? SyncState.ComputeHash(portableLocal)
                : SyncState.ComputeHash(localPath);
        var syncHash = SyncState.ComputeHash(syncPath);
        var baseline = state.GetBaseline(relativePath);
        var legacyBaselineMatchesLocal = portableLocal is not null
            && baseline is not null
            && baseline == SyncState.ComputeHash(localPath);

        if (localHash is null && syncHash is null)
        {
            state.Remove(relativePath);
            return new SyncEntryResult { RelativePath = relativePath, Action = SyncAction.Skipped, Reason = "File missing on both sides." };
        }

        if (localHash is not null && localHash == syncHash)
        {
            // Same chat content, but this PC's copy may still carry another PC's paths
            // (for example, restored before mapping existed): rewrite it for this PC.
            if (mapper is not null && restoreAllowed && NeedsLocalRewrite(mapper, project, localPath, syncPath))
            {
                return Pull(provider, project, relativePath, localPath, syncPath, syncHash, syncRootPath, state, runningProcesses, dryRun, mapper);
            }

            if (!dryRun)
            {
                state.SetBaseline(relativePath, localHash);
            }

            return new SyncEntryResult { RelativePath = relativePath, Action = SyncAction.Unchanged };
        }

        // Older versions stored the hash of the local bytes before content mapping
        // existed. An unchanged local descriptor must not conflict with a portable
        // version pulled from Git; if the archive is still in the old form, migrate
        // it to the portable form instead.
        if (legacyBaselineMatchesLocal && syncHash == baseline)
        {
            return Push(relativePath, localPath, syncPath, localHash, portableLocal, state, dryRun);
        }

        var localChanged = localHash != baseline && !legacyBaselineMatchesLocal;
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

        if (!localChanged && !restoreAllowed && syncHash is not null)
        {
            return new SyncEntryResult
            {
                RelativePath = relativePath,
                Action = SyncAction.Skipped,
                Reason = "This session is not selected for restore on this PC."
            };
        }

        return localChanged
            ? Push(relativePath, localPath, syncPath, localHash, portableLocal, state, dryRun)
            : Pull(provider, project, relativePath, localPath, syncPath, syncHash, syncRootPath, state, runningProcesses, dryRun, mapper);
    }

    /// <summary>
    /// Whether this PC's copy still carries another PC's data and has to be rewritten.
    /// </summary>
    /// <remarks>
    /// A file that differs only in letter case is left alone. The difference is then just
    /// how this PC's own path is spelled — Visual Studio was observed recording the same
    /// folder as both <c>c:\</c> and <c>C:\</c> — and the tool that wrote it may compare
    /// that path as an exact string, so rewriting it could hide a chat that is currently
    /// visible while fixing nothing.
    /// </remarks>
    private static bool NeedsLocalRewrite(IChatContentMapper mapper, ProjectInfo project, string localPath, string syncPath)
    {
        var expected = mapper.ToLocal(project, File.ReadAllBytes(syncPath));
        return !EqualsIgnoringAsciiCase(File.ReadAllBytes(localPath), expected);
    }

    /// <summary>
    /// Byte comparison that ignores ASCII letter case, which is all a path can differ by.
    /// </summary>
    private static bool EqualsIgnoringAsciiCase(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (var index = 0; index < left.Length; index++)
        {
            if (ToLowerAscii(left[index]) != ToLowerAscii(right[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static byte ToLowerAscii(byte value) =>
        value is >= (byte)'A' and <= (byte)'Z' ? (byte)(value + ('a' - 'A')) : value;

    private static SyncEntryResult Push(
        string relativePath,
        string localPath,
        string syncPath,
        string? localHash,
        byte[]? portableLocal,
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

            if (portableLocal is not null)
            {
                File.WriteAllBytes(syncPath, portableLocal);
            }
            else
            {
                File.Copy(localPath, syncPath, overwrite: true);
            }

            state.SetBaseline(relativePath, localHash);
        }

        return new SyncEntryResult { RelativePath = relativePath, Action = SyncAction.Pushed };
    }

    private SyncEntryResult Pull(
        IChatProvider provider,
        ProjectInfo project,
        string relativePath,
        string localPath,
        string syncPath,
        string? syncHash,
        string syncRootPath,
        SyncState state,
        IReadOnlyList<string> runningProcesses,
        bool dryRun,
        IChatContentMapper? mapper)
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

        if (provider is IChatRestoreValidator validator
            && validator.GetRestoreRefusal(project, syncPath) is { } refusal)
        {
            return new SyncEntryResult
            {
                RelativePath = relativePath,
                Action = SyncAction.Skipped,
                Reason = refusal
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

            if (mapper is not null)
            {
                File.WriteAllBytes(localPath, mapper.ToLocal(project, File.ReadAllBytes(syncPath)));
            }
            else
            {
                File.Copy(syncPath, localPath, overwrite: true);
            }

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
