using CodeChatSync.Core;

namespace CodeChatSync.Providers.Claude;

/// <summary>
/// Lists the local folders Claude Code has sessions for, so the user can pick one to
/// register. Candidates come from the <c>cwd</c> recorded inside the transcripts; the
/// encoded folder names are never decoded into paths.
/// </summary>
public static class ClaudeProjectDiscovery
{
    /// <summary>Process names indicating Claude Code is in use, without extension.</summary>
    public static IReadOnlyList<string> ProcessNames { get; } = ["claude"];

    /// <summary>
    /// Returns <c>$CLAUDE_CONFIG_DIR/projects</c> when that variable is set, otherwise
    /// <c>~/.claude/projects</c>.
    /// </summary>
    public static string GetDefaultProjectsRoot()
    {
        var configDirectory = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (string.IsNullOrWhiteSpace(configDirectory) || !Path.IsPathFullyQualified(configDirectory))
        {
            configDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude");
        }

        return Path.Combine(configDirectory, "projects");
    }

    /// <summary>
    /// Returns candidate projects, ordered by most recent session. Folders holding no
    /// session transcript (for example only <c>memory/</c>) and transcripts without a
    /// usable working directory are ignored.
    /// </summary>
    /// <exception cref="ClaudeCodeRunningException">Claude Code is running.</exception>
    public static IReadOnlyList<ClaudeProjectCandidate> DiscoverCandidates(
        IProcessGuard processGuard,
        string? projectsRoot = null)
    {
        EnsureClosed(processGuard);

        var directory = new ClaudeProjectsDirectory(projectsRoot ?? GetDefaultProjectsRoot());
        var sessionsByPath = new Dictionary<string, List<(string FolderName, string SessionId, FileInfo File, string? Title)>>(
            LocalPaths.Comparison == StringComparison.Ordinal ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

        foreach (var folder in directory.EnumerateProjectFolders())
        {
            foreach (var (sessionId, file) in ClaudeProjectsDirectory.EnumerateTranscripts(folder))
            {
                var metadata = TryRead(file, includeTitle: true);
                if (!LocalPaths.TryNormalize(metadata?.WorkingDirectory, out var localPath))
                {
                    continue;
                }

                if (!sessionsByPath.TryGetValue(localPath, out var sessions))
                {
                    sessions = [];
                    sessionsByPath[localPath] = sessions;
                }

                sessions.Add((folder.Name, sessionId, file, metadata!.Title));
            }
        }

        var storageFolders = sessionsByPath.Keys.ToDictionary(
            path => path,
            path => ClaudeProjectFolderName.TryGet(path, out var name) ? name : null,
            sessionsByPath.Comparer);

        var candidates = new List<ClaudeProjectCandidate>(sessionsByPath.Count);
        foreach (var (localPath, sessions) in sessionsByPath)
        {
            var storageFolder = storageFolders[localPath];
            var hasCollision = storageFolder is not null && storageFolders.Any(other =>
                !LocalPaths.AreSame(other.Key, localPath)
                && other.Value is not null
                && ClaudeProjectFolderName.AreSame(other.Value, storageFolder));

            candidates.Add(new ClaudeProjectCandidate
            {
                LocalPath = localPath,
                LocalPathExists = Directory.Exists(localPath),
                StorageFolderName = storageFolder,
                HasStorageCollision = hasCollision,
                Sessions =
                [
                    .. sessions
                        .Select(session => new ClaudeSessionInfo
                        {
                            SessionId = session.SessionId,
                            StorageFolderName = session.FolderName,
                            IsInStorageFolder = storageFolder is not null
                                && ClaudeProjectFolderName.AreSame(session.FolderName, storageFolder),
                            Title = session.Title,
                            Length = session.File.Length,
                            LastWriteTimeUtc = session.File.LastWriteTimeUtc
                        })
                        .OrderByDescending(session => session.LastWriteTimeUtc)
                ]
            });
        }

        return
        [
            .. candidates
                .OrderByDescending(candidate => candidate.Sessions[0].LastWriteTimeUtc)
                .ThenBy(candidate => candidate.LocalPath, StringComparer.OrdinalIgnoreCase)
        ];
    }

    /// <summary>
    /// Whether a candidate working directory belongs to the project rooted at
    /// <paramref name="projectRoot"/>, which it does when it is that folder or sits below
    /// it. Claude Code stores a session under the directory it was started in, so a
    /// project's sessions are spread across its subfolders.
    /// </summary>
    public static bool IsWithinProject(string candidatePath, string projectRoot) =>
        LocalPaths.TryNormalize(candidatePath, out var candidate)
        && LocalPaths.TryNormalize(projectRoot, out var root)
        && LocalPaths.IsWithin(candidate, root);

    /// <summary>Throws when any Claude Code process is running.</summary>
    internal static void EnsureClosed(IProcessGuard processGuard)
    {
        ArgumentNullException.ThrowIfNull(processGuard);

        var running = processGuard.GetRunningProcesses(ProcessNames);
        if (running.Count > 0)
        {
            throw new ClaudeCodeRunningException(running);
        }
    }

    /// <summary>Reads transcript metadata, treating an unreadable file as having none.</summary>
    internal static TranscriptMetadata? TryRead(FileInfo file, bool includeTitle)
    {
        try
        {
            return ClaudeTranscriptReader.Read(file.FullName, includeTitle);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
