using CodeChatSync.Core;

namespace CodeChatSync.Providers.Claude;

/// <summary>
/// Exposes Claude Code session transcripts to the Core.
/// </summary>
/// <remarks>
/// <para>
/// Claude Code stores sessions per user under <c>~/.claude/projects/&lt;encoded-cwd&gt;/</c>.
/// The format is undocumented, so everything here is best-effort and stays inside this
/// project.
/// </para>
/// <para>
/// Binding: <see cref="ProjectInfo.LocalPath"/> is the project's repository root. A
/// transcript belongs to the project when its recorded <c>cwd</c> is that path or sits
/// below it; the encoded folder name is used solely to locate storage, never as identity.
/// </para>
/// <para>
/// Claude Code derives a session's storage folder from the directory it was started in,
/// so one project spreads across as many folders as the subdirectories it was run from —
/// a repository root and its <c>src</c> are two different folders. Each transcript is
/// therefore archived under the path of its working directory relative to the project
/// root, for example <c>src/&lt;session-id&gt;.jsonl</c>, with sessions started at the
/// root keeping the plain <c>&lt;session-id&gt;.jsonl</c>. That relative path is the same
/// on every PC, which is what lets a restore rebuild the right storage folder locally.
/// </para>
/// <para>
/// Scope: only top-level <c>&lt;session-id&gt;.jsonl</c> transcripts are synced.
/// Per-session artifact folders (for example <c>&lt;session-id&gt;/subagents/</c>),
/// <c>memory/</c> and any other file are not synced, since their contents have not been
/// verified to be free of per-PC or global data.
/// </para>
/// <para>
/// Safety: nothing is read or resolved while a <c>claude</c> process is running; the
/// calls throw <see cref="ClaudeCodeRunningException"/> instead, because the Core hashes
/// local files it maps. Restores are refused when the target storage folder is shared
/// with a different local path, or when it or the target file is a link.
/// </para>
/// <para>
/// Path remapping: a transcript's absolute path is not confined to <c>cwd</c> — it is
/// recorded on most lines, in tool parameters, and in free text such as system-reminder
/// attachments and the assistant's own prose. <see cref="ClaudeTranscriptPathMapper"/>
/// therefore treats the whole file as text and replaces every occurrence of the project's
/// root, so a chat restored on a PC where the project lives at a different path keeps a
/// working, consistent history instead of referring to a folder that does not exist
/// there. A transcript archived before this existed still carries its source PC's raw
/// path and restores only where that already matches, exactly as before.
/// </para>
/// </remarks>
public sealed class ClaudeCodeChatProvider : IChatSessionProvider, IChatRestoreValidator, IChatContentMapper
{
    private readonly IProcessGuard _processGuard;
    private readonly ClaudeProjectsDirectory _directory;

    public ClaudeCodeChatProvider(IProcessGuard processGuard, string? projectsRoot = null)
    {
        _processGuard = processGuard ?? throw new ArgumentNullException(nameof(processGuard));
        _directory = new ClaudeProjectsDirectory(projectsRoot ?? ClaudeProjectDiscovery.GetDefaultProjectsRoot());
    }

    public string Id => "claudecode";

    public IReadOnlyList<string> ProcessNames => ClaudeProjectDiscovery.ProcessNames;

    /// <summary>Claude Code's projects directory on this PC.</summary>
    public string ProjectsRoot => _directory.Root;

    /// <exception cref="ClaudeCodeRunningException">Claude Code is running.</exception>
    public IEnumerable<ChatLocation> Discover(ProjectInfo project)
    {
        ArgumentNullException.ThrowIfNull(project);
        ClaudeProjectDiscovery.EnsureClosed(_processGuard);

        var projectRoot = NormalizeProjectPath(project);

        // Resolved up front so a project whose path Claude Code cannot store, or whose own
        // folder is a link, fails with that reason rather than silently finding nothing.
        ResolveStorageFolder(projectRoot);

        var locations = new List<ChatLocation>();

        // Every storage folder is examined: the project's sessions are spread across one
        // per directory Claude Code was started in, and only the recorded cwd says which
        // folder holds what.
        foreach (var folder in _directory.EnumerateProjectFolders())
        {
            foreach (var (sessionId, file) in ClaudeProjectsDirectory.EnumerateTranscripts(folder))
            {
                var metadata = ClaudeProjectDiscovery.TryRead(file, includeTitle: false);
                if (!LocalPaths.TryNormalize(metadata?.WorkingDirectory, out var workingDirectory)
                    || !LocalPaths.IsWithin(workingDirectory, projectRoot))
                {
                    continue;
                }

                // The transcript must sit where its own working directory says it should.
                // Anything else would be archived under a path that moves it on restore.
                if (!ClaudeProjectFolderName.TryGet(workingDirectory, out var expectedFolder)
                    || !ClaudeProjectFolderName.AreSame(expectedFolder, folder.Name))
                {
                    continue;
                }

                locations.Add(new ChatLocation
                {
                    RelativePath = BuildRelativePath(
                        LocalPaths.GetRelativeDirectory(workingDirectory, projectRoot), sessionId),
                    LocalPath = file.FullName,
                    Length = file.Length,
                    LastWriteTimeUtc = file.LastWriteTimeUtc
                });
            }
        }

        return locations;
    }

    /// <summary>
    /// Archive path of a transcript: its working directory relative to the project root,
    /// then the session file. A session started at the root keeps the plain file name.
    /// </summary>
    private static string BuildRelativePath(string relativeDirectory, string sessionId)
    {
        var fileName = sessionId + ClaudeProjectsDirectory.TranscriptExtension;
        return relativeDirectory.Length == 0 ? fileName : $"{relativeDirectory}/{fileName}";
    }

    /// <exception cref="ClaudeCodeRunningException">Claude Code is running.</exception>
    /// <exception cref="ArgumentException">The path is not a session transcript.</exception>
    /// <exception cref="InvalidOperationException">The storage is shared with another path or is a link.</exception>
    public string MapToLocal(ProjectInfo project, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(project);
        var sessionId = GetSessionId(relativePath);
        ClaudeProjectDiscovery.EnsureClosed(_processGuard);

        var projectRoot = NormalizeProjectPath(project);
        if (!Directory.Exists(projectRoot))
        {
            throw new InvalidOperationException(
                "The project's local folder does not exist on this PC; register its current location first.");
        }

        // The archived path says which directory the session was started in, so the
        // storage folder is rebuilt from this PC's copy of that directory.
        var workingDirectory = ResolveWorkingDirectory(projectRoot, relativePath);
        var folder = ResolveStorageFolder(workingDirectory);
        EnsureNoCollision(folder, workingDirectory);

        var target = new FileInfo(Path.Combine(folder.FullName, sessionId + ClaudeProjectsDirectory.TranscriptExtension));
        if (LocalPaths.IsReparsePoint(target))
        {
            throw new InvalidOperationException($"The local transcript for session '{sessionId}' is a link.");
        }

        return target.FullName;
    }

    public string? GetRestoreRefusal(ProjectInfo project, string archivedPath)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(archivedPath);
        ClaudeProjectDiscovery.EnsureClosed(_processGuard);

        // Already portabilized by a push from some PC: ToLocal rewrites every occurrence
        // of the marker to this PC's own project path, wherever that is.
        if (ClaudeTranscriptPathMapper.IsPortable(File.ReadAllBytes(archivedPath)))
        {
            return null;
        }

        // Archived before this PC's provider learned to remap paths: it still carries its
        // source PC's raw absolute path, and is only safe to restore where that already
        // matches. It becomes restorable anywhere once some PC pushes it again.
        var projectRoot = NormalizeProjectPath(project);
        var metadata = ClaudeTranscriptReader.Read(archivedPath, includeTitle: false);
        return LocalPaths.TryNormalize(metadata.WorkingDirectory, out var archivedPathCwd)
            && LocalPaths.IsWithin(archivedPathCwd, projectRoot)
                ? null
                : "This chat was archived before path remapping, from a PC where the project lives "
                + "at a different path. It will restore here once a PC with this project at a known "
                + "path syncs it again.";
    }

    /// <summary>Every transcript file may carry the project's absolute path.</summary>
    public bool IsMapped(string relativePath) => IsTranscriptPath(relativePath);

    public byte[] ToPortable(ProjectInfo project, byte[] localContent)
    {
        ArgumentNullException.ThrowIfNull(project);
        return ClaudeTranscriptPathMapper.ToPortable(localContent, project.LocalPath);
    }

    public byte[] ToLocal(ProjectInfo project, byte[] portableContent)
    {
        ArgumentNullException.ThrowIfNull(project);
        return ClaudeTranscriptPathMapper.ToLocal(portableContent, project.LocalPath);
    }

    private static bool IsTranscriptPath(string relativePath)
    {
        var normalized = RelativePathGuard.Normalize(relativePath);
        var separator = normalized.LastIndexOf('/');
        var fileName = separator < 0 ? normalized : normalized[(separator + 1)..];
        return ClaudeProjectsDirectory.TryGetSessionId(fileName, out _);
    }

    /// <summary>
    /// Returns the session ID of a sync-folder path, which is the file name of
    /// <c>&lt;working-directory&gt;/&lt;session-id&gt;.jsonl</c>.
    /// </summary>
    public string GetSessionId(string relativePath)
    {
        var normalized = RelativePathGuard.Normalize(relativePath);
        var separator = normalized.LastIndexOf('/');
        var fileName = separator < 0 ? normalized : normalized[(separator + 1)..];

        if (!ClaudeProjectsDirectory.TryGetSessionId(fileName, out var sessionId))
        {
            throw new ArgumentException(
                $"Expected a Claude Code session transcript ('<session-id>.jsonl'): '{relativePath}'.",
                nameof(relativePath));
        }

        return sessionId;
    }

    private static string NormalizeProjectPath(ProjectInfo project)
    {
        if (!LocalPaths.TryNormalize(project.LocalPath, out var projectRoot))
        {
            throw new ArgumentException("The project's local path must be absolute.", nameof(project));
        }

        return projectRoot;
    }

    /// <summary>
    /// This PC's copy of the directory the session was started in, taken from the archived
    /// path so it lands in the storage folder Claude Code will look in.
    /// </summary>
    private static string ResolveWorkingDirectory(string projectRoot, string relativePath)
    {
        var normalized = RelativePathGuard.Normalize(relativePath);
        var separator = normalized.LastIndexOf('/');
        if (separator < 0)
        {
            return projectRoot;
        }

        return LocalPaths.TryNormalize(RelativePathGuard.ResolveUnder(projectRoot, normalized[..separator]), out var directory)
            ? directory
            : projectRoot;
    }

    private DirectoryInfo ResolveStorageFolder(string workingDirectory)
    {
        if (!ClaudeProjectFolderName.TryGet(workingDirectory, out var folderName))
        {
            throw new NotSupportedException(
                $"'{workingDirectory}' is longer than Claude Code's {ClaudeProjectFolderName.MaxLength}-character storage name limit.");
        }

        return _directory.GetProjectFolder(folderName);
    }

    /// <summary>
    /// Refuses a storage folder that also holds sessions of a different local path mapping
    /// to the same name, since Claude Code would show restored chats in both projects.
    /// Transcripts restored from another PC record a path that maps elsewhere, so they do
    /// not count as a collision.
    /// </summary>
    private static void EnsureNoCollision(DirectoryInfo folder, string localPath)
    {
        foreach (var (_, file) in ClaudeProjectsDirectory.EnumerateTranscripts(folder))
        {
            var metadata = ClaudeProjectDiscovery.TryRead(file, includeTitle: false);
            if (!LocalPaths.TryNormalize(metadata?.WorkingDirectory, out var workingDirectory)
                || LocalPaths.AreSame(workingDirectory, localPath))
            {
                continue;
            }

            if (ClaudeProjectFolderName.TryGet(workingDirectory, out var otherFolder)
                && ClaudeProjectFolderName.AreSame(otherFolder, folder.Name))
            {
                throw new InvalidOperationException(
                    $"The Claude Code folder '{folder.Name}' is shared with another local path; restore is refused to avoid mixing projects.");
            }
        }
    }
}
