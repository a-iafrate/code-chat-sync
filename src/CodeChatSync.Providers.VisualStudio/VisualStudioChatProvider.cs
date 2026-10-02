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
public sealed class VisualStudioChatProvider
    : IChatSessionRegistrar, IChatContentMapper, IChatRestoreValidator, IDerivedChatContent,
        IArchivedChatCatalog, IArchivedChatReader, IArchivedChatRenamer
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

    /// <summary>What this PC rebuilt, per project, loaded once per run.</summary>
    private readonly Dictionary<string, CopilotRebuiltRecords> _rebuilt = new(StringComparer.OrdinalIgnoreCase);

    private readonly string? _titleBackupDirectory;

    public VisualStudioChatProvider(
        string? sessionStateRoot = null,
        string? sessionStorePath = null,
        string? titleBackupDirectory = null)
    {
        _titleBackupDirectory = titleBackupDirectory;
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

        var locations = new List<ChatLocation>();
        var openSessionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var sessions = SyncTrace.Time(
            "VisualStudio.Discover: CopilotChatDiscovery.DiscoverSessions",
            () => CopilotChatDiscovery.DiscoverSessions(_sessionStateRoot));
        SyncTrace.Log($"VisualStudio.Discover: {sessions.Count} session(s) found under {_sessionStateRoot}");

        foreach (var session in sessions)
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
                locations.Add(new ChatLocation
                {
                    RelativePath = relativePath,
                    LocalPath = Path.Combine(session.SessionDirectory, file.RelativePath),
                    Length = file.Length,
                    LastWriteTimeUtc = file.LastWriteTime,
                    IsInUse = session.IsInUse
                });
            }
        }

        SyncTrace.Log($"VisualStudio.Discover: {locations.Count} transcript file(s) after filtering to this project");

        // The Chat window's own record of each conversation, which is what makes a chat
        // appear in the list at all. Discovered independently of the transcripts: a chat
        // that never used the CLI agent has one of these and no session folder.
        var rebuilt = GetRebuiltRecords(project);
        var windowEntries = SyncTrace.Time(
            "VisualStudio.Discover: CopilotChatWindowStore.Discover (.vs tree walk)",
            () => CopilotChatWindowStore.Discover(project.LocalPath));
        SyncTrace.Log($"VisualStudio.Discover: {windowEntries.Count} chat window record(s) found under {project.LocalPath}");

        foreach (var entry in windowEntries)
        {
            var info = new FileInfo(entry.LocalPath);

            // A record this PC rebuilt from an already-synced transcript is not published:
            // the other PC can rebuild its own, and two rebuilds never match byte for byte.
            if (!info.Exists || rebuilt.IsStillOurs(entry.SessionId, entry.LocalPath))
            {
                continue;
            }

            locations.Add(new ChatLocation
            {
                RelativePath = CopilotChatWindowStore.BuildRelativePath(entry.SessionId, entry.SolutionRelativePath),
                LocalPath = entry.LocalPath,
                Length = info.Length,
                LastWriteTimeUtc = info.LastWriteTimeUtc,

                // Visual Studio rewrites this record as the conversation goes on, so an
                // open chat's entry is as unsafe to copy as its transcript.
                IsInUse = openSessionIds.Contains(entry.SessionId)
            });
        }

        return locations;
    }

    /// <summary>
    /// Describes the sessions archived for a project, from each session's own descriptor.
    /// </summary>
    /// <remarks>
    /// Sessions that exist only as a Chat window record, with no descriptor, are not listed
    /// yet: the restore selection list has the same limit, and rendering them means decoding
    /// the record's header and first message. Times come from the descriptor, which the
    /// originating PC wrote, never from file timestamps — a <c>git pull</c> stamps every
    /// file with the moment of the checkout.
    /// </remarks>
    public IReadOnlyList<ArchivedChat> ListArchivedChats(string projectSyncFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectSyncFolder);

        return
        [
            .. CopilotChatDiscovery.DiscoverSessions(projectSyncFolder)
                .Select(session => new ArchivedChat(
                    Id,
                    Path.GetFileName(session.SessionDirectory),
                    CopilotChatTitle.Clean(session.Name),
                    session.CreatedAt,
                    session.UpdatedAt,
                    session.Files.Sum(file => file.Length),
                    session.Files.Count))
        ];
    }

    /// <summary>
    /// Renames an archived session by rewriting its descriptor, as Visual Studio does. Other
    /// PCs pick the name up when they restore the chat; a PC that already lists it takes the
    /// new name at the next sync.
    /// </summary>
    public bool RenameArchivedChat(string projectSyncFolder, string chatId, string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectSyncFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(chatId);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        // Resolved under the project's folder, so a hostile id cannot reach outside it.
        var sessionDirectory = RelativePathGuard.ResolveUnder(projectSyncFolder, chatId);
        return Directory.Exists(sessionDirectory)
            && CopilotDescriptorRenamer.Rename(sessionDirectory, title, _titleBackupDirectory);
    }

    /// <summary>
    /// Reads an archived session's conversation from its <c>events.jsonl</c>.
    /// </summary>
    /// <remarks>
    /// A session that exists only as a Chat window record has no transcript to read and
    /// returns <see langword="null"/>. Nothing in a transcript is a path this tool maps, so
    /// <paramref name="projectRoot"/> is not needed.
    /// </remarks>
    public ArchivedChatContent? ReadArchivedChat(string projectSyncFolder, string chatId, string? projectRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectSyncFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(chatId);

        // Resolved under the project's folder, so a hostile id cannot reach outside it.
        var transcript = RelativePathGuard.ResolveUnder(
            projectSyncFolder, $"{chatId}/{CopilotChatDiscovery.GetTranscriptRelativePath()}");
        if (!File.Exists(transcript))
        {
            return null;
        }

        var builder = new ArchivedChatContentBuilder();
        CopilotTranscriptMessages.ReadInto(transcript, builder);
        return builder.Build();
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
        SyncTrace.Log($"VisualStudio.RegisterSessions: {sessions.Length} session(s) selected for registration");

        var registration = SyncTrace.Time(
            "VisualStudio.RegisterSessions: CopilotSessionStore.Register (SQLite index)",
            () => _sessionStore.Register(sessions, dryRun));
        var listing = SyncTrace.Time(
            "VisualStudio.RegisterSessions: EnsureListed (.vs chat window records)",
            () => EnsureListed(project, sessions, dryRun));

        return new SessionRegistrationResult
        {
            RegisteredCount = registration.RegisteredCount,
            ListedCount = listing.Count,
            Reason = Combine(registration.Reason, listing.Reason)
        };
    }

    /// <summary>
    /// Gives a restored chat the Chat window record it needs to be listed and read, and
    /// refreshes one this PC rebuilt once the conversation has moved on elsewhere.
    /// </summary>
    /// <remarks>
    /// A record Visual Studio owns is never touched: it carries detail a rebuild leaves
    /// out, and overwriting it would throw that away. A record this PC rebuilt is ours to
    /// redo, which is how a chat continued on another PC shows its new exchanges here.
    /// </remarks>
    private (int Count, string? Reason) EnsureListed(
        ProjectInfo project,
        IReadOnlyList<CopilotChatSession> sessions,
        bool dryRun)
    {
        var rebuilt = GetRebuiltRecords(project);
        var existing = SyncTrace.Time(
            "EnsureListed: CopilotChatWindowStore.Discover (existing records)",
            () => CopilotChatWindowStore.Discover(project.LocalPath))
            .GroupBy(entry => entry.SessionId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().LocalPath, StringComparer.OrdinalIgnoreCase);

        // A chat Visual Studio currently has open is left alone: it may write the record
        // itself at any moment, and racing it is the same hazard as copying a live file.
        var pending = sessions
            .Where(session => !session.IsInUse && NeedsRecord(session, existing, rebuilt))
            .ToArray();
        SyncTrace.Log($"EnsureListed: {pending.Length} session(s) pending a chat window record");
        if (pending.Length == 0)
        {
            return (0, null);
        }

        var directory = SyncTrace.Time(
            "EnsureListed: CopilotChatWindowStore.FindBusiestSessionsDirectory",
            () => CopilotChatWindowStore.FindBusiestSessionsDirectory(project.LocalPath));
        var template = SyncTrace.Time(
            "EnsureListed: CopilotChatWindowStore.TryFindTemplate",
            () => CopilotChatWindowStore.TryFindTemplate(project.LocalPath));
        if (directory is null || template is null)
        {
            return (0, "Visual Studio has not opened a Copilot chat in this project on this PC yet, so restored "
                + "chats have no list to appear in. Start a chat here once, then sync again.");
        }

        var written = 0;
        var changed = false;
        foreach (var session in pending)
        {
            var transcriptPath = Path.Combine(session.SessionDirectory, CopilotChatDiscovery.GetTranscriptRelativePath());
            var transcriptSize = File.Exists(transcriptPath) ? new FileInfo(transcriptPath).Length : 0;
            var turns = SyncTrace.Time(
                $"EnsureListed: parse transcript {session.Id} ({transcriptSize / 1024.0:N0} KB)",
                () => CopilotTranscriptTurns.Read(transcriptPath));
            if (turns.Count == 0)
            {
                continue;
            }

            if (dryRun)
            {
                written++;
                continue;
            }

            // The descriptor's own times come from the PC that held the conversation, so
            // the list shows each restored chat at its real age.
            var record = SyncTrace.Time(
                $"EnsureListed: build record {session.Id} ({turns.Count} turn(s))",
                () => CopilotChatWindowRecord.TryBuild(
                    template.Value,
                    session.Id,
                    turns,
                    session.CreatedAt,
                    session.UpdatedAt ?? session.CreatedAt));

            if (record is null)
            {
                continue;
            }

            try
            {
                // Refreshed in place when it already exists, so a chat does not move
                // between solutions behind the user's back.
                var target = existing.TryGetValue(session.Id, out var current) ? current : Path.Combine(directory, session.Id);
                SyncTrace.Time(
                    $"EnsureListed: write record {session.Id} ({record.Length / 1024.0:N0} KB) to {target}",
                    () => File.WriteAllBytes(target, record));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Persist(rebuilt, changed);
                return (written, $"Could not add restored chats to Visual Studio's chat list: {exception.Message}");
            }

            rebuilt.Remember(session.Id, record);
            changed = true;
            written++;
        }

        Persist(rebuilt, changed);
        return (written, null);
    }

    /// <summary>
    /// Whether a chat needs a record written: it has none, or the one it has is this PC's
    /// own rebuild and the transcript has grown past it.
    /// </summary>
    private static bool NeedsRecord(
        CopilotChatSession session,
        IReadOnlyDictionary<string, string> existing,
        CopilotRebuiltRecords rebuilt)
    {
        if (!existing.TryGetValue(session.Id, out var recordPath))
        {
            return true;
        }

        if (!rebuilt.IsStillOurs(session.Id, recordPath))
        {
            return false;
        }

        var transcript = Path.Combine(session.SessionDirectory, CopilotChatDiscovery.GetTranscriptRelativePath());
        return File.Exists(transcript)
            && File.GetLastWriteTimeUtc(transcript) > File.GetLastWriteTimeUtc(recordPath);
    }

    private static void Persist(CopilotRebuiltRecords rebuilt, bool changed)
    {
        if (!changed)
        {
            return;
        }

        try
        {
            rebuilt.Save();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Only costs the next run the knowledge that these records are rebuilds.
        }
    }

    private static string? Combine(string? first, string? second) =>
        (first, second) switch
        {
            (null or "", null or "") => null,
            (null or "", var only) => only,
            (var only, null or "") => only,
            _ => first + Environment.NewLine + second
        };

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

    /// <summary>
    /// Whether a Chat window record is still the one this PC rebuilt, rather than one
    /// Visual Studio has since written itself.
    /// </summary>
    public bool IsDerivedLocally(ProjectInfo project, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!CopilotChatWindowStore.TryParseRelativePath(relativePath, out _))
        {
            return false;
        }

        var sessionId = GetSessionId(relativePath);
        return GetRebuiltRecords(project).IsStillOurs(sessionId, MapToLocal(project, relativePath));
    }

    private CopilotRebuiltRecords GetRebuiltRecords(ProjectInfo project)
    {
        var key = project.SyncFolderName;
        if (!_rebuilt.TryGetValue(key, out var records))
        {
            records = CopilotRebuiltRecords.Load(CopilotRebuiltRecords.GetDefaultPath(Id, project));
            _rebuilt[key] = records;
        }

        return records;
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
