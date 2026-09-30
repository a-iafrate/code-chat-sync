using CodeChatSync.Core;

namespace CodeChatSync.Providers.VisualStudio;

/// <summary>
/// Exposes Visual Studio's Copilot chat sessions to the Core.
/// </summary>
/// <remarks>
/// Sessions live outside the solution, under
/// <c>%LOCALAPPDATA%\Microsoft\VisualStudio\CopilotCli\session-state\&lt;session-id&gt;</c>.
/// The layout is undocumented, so everything here is best-effort and stays inside
/// this project.
/// </remarks>
public sealed class VisualStudioChatProvider : IChatSessionRegistrar, IChatContentMapper, IChatRestoreValidator
{
    /// <summary>
    /// Runtime state that is specific to one PC or to a live session, and therefore
    /// must never be copied between machines.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The transcript lives entirely in <c>events.jsonl</c>; the descriptor lives in
    /// <c>workspace.yaml</c>. Both are synced.
    /// </para>
    /// <para>
    /// A session's <c>session.db</c> is excluded: it is the agent's scratch database
    /// (todo and inbox tables), it is absent from most sessions, and the richest
    /// transcripts observed had no such file at all. Losing it does not lose chat
    /// content.
    /// </para>
    /// <para>
    /// The machine-wide <c>session-store.db</c> one level above the session folders
    /// is never a candidate for syncing: it indexes the sessions of <em>every</em>
    /// repository on the PC, so copying it into a sync repository would mix
    /// unrelated projects' data. A restored session does, however, need a row in the
    /// local copy of it before Visual Studio lists the chat, which
    /// <see cref="CopilotSessionStore"/> inserts without ever moving the file.
    /// </para>
    /// </remarks>
    private static readonly string[] ExcludedExtensions =
    [
        ".lock",
        ".db",
        ".db-shm",
        ".db-wal"
    ];

    private readonly string _sessionStateRoot;
    private readonly CopilotSessionStore _sessionStore;

    /// <summary>
    /// This PC's chat workspace folder per project, looked up once per run: it never
    /// changes while a sync is in flight, and the lookup walks the project tree.
    /// </summary>
    private readonly Dictionary<string, string?> _workspaceIds = new(StringComparer.OrdinalIgnoreCase);

    public VisualStudioChatProvider(string? sessionStateRoot = null, string? sessionStorePath = null)
    {
        _sessionStateRoot = sessionStateRoot ?? CopilotChatDiscovery.GetDefaultSessionStateRoot();
        _sessionStore = new CopilotSessionStore(
            sessionStorePath ?? CopilotSessionStore.GetDefaultPath(_sessionStateRoot));
    }

    public string Id => "visualstudio";

    public IReadOnlyList<string> ProcessNames { get; } = ["devenv"];

    /// <summary>Root under which this PC stores Copilot chat sessions.</summary>
    public string SessionStateRoot => _sessionStateRoot;

    public IEnumerable<ChatLocation> Discover(ProjectInfo project)
    {
        ArgumentNullException.ThrowIfNull(project);

        var openSessionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var session in CopilotChatDiscovery.DiscoverSessions(_sessionStateRoot))
        {
            if (!BelongsToProject(session, project))
            {
                continue;
            }

            if (session.IsInUse)
            {
                openSessionIds.Add(session.Id);
            }

            foreach (var file in session.Files)
            {
                if (IsExcluded(file.RelativePath))
                {
                    continue;
                }

                var relativePath = $"{session.Id}/{file.RelativePath.Replace('\\', '/')}";
                yield return new ChatLocation
                {
                    RelativePath = relativePath,
                    LocalPath = Path.Combine(session.SessionDirectory, file.RelativePath),
                    Length = file.Length,
                    LastWriteTimeUtc = file.LastWriteTime,
                    IsInUse = session.IsInUse
                };
            }
        }

        // The Chat window's own record of each conversation, which is what makes a chat
        // appear in the list at all. Discovered independently of the transcripts: a chat
        // that never used the CLI agent has one of these and no session folder.
        foreach (var entry in CopilotChatWindowStore.Discover(project.LocalPath))
        {
            var info = new FileInfo(entry.LocalPath);
            if (!info.Exists)
            {
                continue;
            }

            yield return new ChatLocation
            {
                RelativePath = CopilotChatWindowStore.BuildRelativePath(entry.SessionId, entry.SolutionRelativePath),
                LocalPath = entry.LocalPath,
                Length = info.Length,
                LastWriteTimeUtc = info.LastWriteTimeUtc,

                // Visual Studio rewrites this record as the conversation goes on, so an
                // open chat's entry is as unsafe to copy as its transcript.
                IsInUse = openSessionIds.Contains(entry.SessionId)
            };
        }
    }

    public string MapToLocal(ProjectInfo project, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(project);

        if (CopilotChatWindowStore.TryParseRelativePath(relativePath, out var solutionRelativePath))
        {
            return CopilotChatWindowStore.MapToLocal(
                project.LocalPath,
                solutionRelativePath,
                GetSessionId(relativePath),
                GetWorkspaceId(project));
        }

        return RelativePathGuard.ResolveUnder(_sessionStateRoot, relativePath);
    }

    public string GetSessionId(string relativePath)
    {
        var normalized = RelativePathGuard.Normalize(relativePath);
        var separator = normalized.IndexOf('/');
        if (separator <= 0 || separator == normalized.Length - 1)
        {
            throw new ArgumentException("Expected a session ID followed by a file path.", nameof(relativePath));
        }

        return normalized[..separator];
    }

    /// <summary>
    /// Adds the restorable sessions that are present on this PC to Visual Studio's own
    /// chat index, which is what decides whether a chat is listed at all.
    /// </summary>
    /// <remarks>
    /// Only sessions belonging to <paramref name="project"/> and already on disk are
    /// considered, so this never announces a chat whose files are missing or one from a
    /// project that is not being synced.
    /// </remarks>
    public SessionRegistrationResult RegisterSessions(
        ProjectInfo project,
        IReadOnlyCollection<string> sessionIds,
        bool dryRun)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sessionIds);

        var selected = new HashSet<string>(sessionIds, StringComparer.OrdinalIgnoreCase);
        var sessions = CopilotChatDiscovery.DiscoverSessions(_sessionStateRoot)
            .Where(session => selected.Contains(session.Id) && BelongsToProject(session, project))
            .ToArray();

        var registration = _sessionStore.Register(sessions, dryRun);
        return new SessionRegistrationResult
        {
            RegisteredCount = registration.RegisteredCount,
            Reason = registration.Reason
        };
    }

    /// <summary>
    /// Refuses to restore a Chat window record while this PC has nowhere to put it.
    /// </summary>
    /// <remarks>
    /// The folder Visual Studio reads is named after a value it generates itself, which
    /// cannot be derived, so it only exists once a chat has been opened in this project
    /// here. Guessing a name would write a file Visual Studio never looks at.
    /// </remarks>
    public string? GetRestoreRefusal(ProjectInfo project, string archivedPath)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(archivedPath);

        var isChatWindowRecord = archivedPath
            .Replace('\\', '/')
            .Contains($"/{CopilotChatWindowStore.PathSegment}/", StringComparison.Ordinal);

        if (!isChatWindowRecord || GetWorkspaceId(project) is not null)
        {
            return null;
        }

        return "Visual Studio has not opened a Copilot chat in this project on this PC yet, "
            + "so there is nowhere to put the entry that lists it. Start a chat here once, then sync again.";
    }

    private string? GetWorkspaceId(ProjectInfo project)
    {
        var key = Path.GetFullPath(project.LocalPath);
        if (!_workspaceIds.TryGetValue(key, out var workspaceId))
        {
            workspaceId = CopilotChatWindowStore.FindWorkspaceId(key);
            _workspaceIds[key] = workspaceId;
        }

        return workspaceId;
    }

    /// <summary>Only a session's <c>workspace.yaml</c> embeds this PC's paths.</summary>
    public bool IsMapped(string relativePath)
    {
        var segments = RelativePathGuard.Normalize(relativePath).Split('/');
        return segments.Length == 2
            && string.Equals(segments[1], CopilotChatDiscovery.WorkspaceDescriptorFileName, StringComparison.OrdinalIgnoreCase);
    }

    public byte[] ToPortable(ProjectInfo project, byte[] localContent)
    {
        ArgumentNullException.ThrowIfNull(project);
        return WorkspaceDescriptorPathMapper.ToPortable(localContent, project.LocalPath);
    }

    public byte[] ToLocal(ProjectInfo project, byte[] portableContent)
    {
        ArgumentNullException.ThrowIfNull(project);
        return WorkspaceDescriptorPathMapper.ToLocal(portableContent, project.LocalPath, Directory.Exists);
    }

    /// <summary>
    /// Matches a session to a project by Git remote first, falling back to the
    /// recorded working directory when the session has no repository.
    /// </summary>
    private static bool BelongsToProject(CopilotChatSession session, ProjectInfo project)
    {
        if (ProjectIdentity.TryFromRemote(session.Repository, out var sessionIdentity)
            && sessionIdentity is not null)
        {
            if (string.Equals(
                    sessionIdentity.NormalizedRemote,
                    project.Identity.NormalizedRemote,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return IsWithin(session.GitRoot ?? session.WorkingDirectory, project.LocalPath);
    }

    private static bool IsExcluded(string relativePath)
    {
        var fileName = Path.GetFileName(relativePath);
        return ExcludedExtensions.Any(extension =>
            fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsWithin(string? candidate, string root)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        string fullCandidate;
        string fullRoot;
        try
        {
            fullCandidate = Trim(Path.GetFullPath(candidate));
            fullRoot = Trim(Path.GetFullPath(root));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (string.Equals(fullCandidate, fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return fullCandidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string Trim(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
