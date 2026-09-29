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
/// Binding: <see cref="ProjectInfo.LocalPath"/> is treated as the working directory the
/// user confirmed when registering the project (see
/// <see cref="ClaudeProjectDiscovery.DiscoverCandidates"/>). A transcript belongs to the
/// project only when its recorded <c>cwd</c> equals that path; the encoded folder name is
/// used solely to locate storage, never as identity.
/// </para>
/// <para>
/// Scope: only top-level <c>&lt;session-id&gt;.jsonl</c> transcripts are synced, as
/// <c>&lt;session-id&gt;.jsonl</c> in the sync folder. Per-session artifact folders (for
/// example <c>&lt;session-id&gt;/subagents/</c>), <c>memory/</c> and any other file are not
/// synced, since their contents have not been verified to be free of per-PC or global
/// data. Sessions started in a subfolder of the project are stored by Claude Code under a
/// different folder and are not included.
/// </para>
/// <para>
/// Safety: nothing is read or resolved while a <c>claude</c> process is running; the
/// calls throw <see cref="ClaudeCodeRunningException"/> instead, because the Core hashes
/// local files it maps. Restores are refused when the target storage folder is shared
/// with a different local path, or when it or the target file is a link.
/// </para>
/// </remarks>
public sealed class ClaudeCodeChatProvider : IChatSessionProvider, IChatRestoreValidator
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

        var (localPath, folder) = ResolveStorage(project);
        var locations = new List<ChatLocation>();
        foreach (var (sessionId, file) in ClaudeProjectsDirectory.EnumerateTranscripts(folder))
        {
            var metadata = ClaudeProjectDiscovery.TryRead(file, includeTitle: false);
            if (!LocalPaths.TryNormalize(metadata?.WorkingDirectory, out var workingDirectory)
                || !LocalPaths.AreSame(workingDirectory, localPath))
            {
                continue;
            }

            locations.Add(new ChatLocation
            {
                RelativePath = sessionId + ClaudeProjectsDirectory.TranscriptExtension,
                LocalPath = file.FullName,
                Length = file.Length,
                LastWriteTimeUtc = file.LastWriteTimeUtc
            });
        }

        return locations;
    }

    /// <exception cref="ClaudeCodeRunningException">Claude Code is running.</exception>
    /// <exception cref="ArgumentException">The path is not a session transcript.</exception>
    /// <exception cref="InvalidOperationException">The storage is shared with another path or is a link.</exception>
    public string MapToLocal(ProjectInfo project, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(project);
        var sessionId = GetSessionId(relativePath);
        ClaudeProjectDiscovery.EnsureClosed(_processGuard);

        var (localPath, folder) = ResolveStorage(project);
        if (!Directory.Exists(localPath))
        {
            throw new InvalidOperationException(
                "The project's local folder does not exist on this PC; register its current location first.");
        }

        EnsureNoCollision(folder, localPath);

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

        var (localPath, _) = ResolveStorage(project);
        var metadata = ClaudeTranscriptReader.Read(archivedPath, includeTitle: false);
        return LocalPaths.TryNormalize(metadata.WorkingDirectory, out var archivedPathCwd)
            && LocalPaths.AreSame(archivedPathCwd, localPath)
                ? null
                : "The archived Claude transcript's cwd does not match this PC's project path; restore is refused until path remapping is supported.";
    }

    /// <summary>
    /// Returns the session ID of a sync-folder path, accepting only
    /// <c>&lt;session-id&gt;.jsonl</c> at the project's top level.
    /// </summary>
    public string GetSessionId(string relativePath)
    {
        var normalized = RelativePathGuard.Normalize(relativePath);
        if (normalized.Contains('/')
            || !ClaudeProjectsDirectory.TryGetSessionId(normalized, out var sessionId))
        {
            throw new ArgumentException(
                $"Expected a Claude Code session transcript ('<session-id>.jsonl'): '{relativePath}'.",
                nameof(relativePath));
        }

        return sessionId;
    }

    private (string LocalPath, DirectoryInfo Folder) ResolveStorage(ProjectInfo project)
    {
        if (!LocalPaths.TryNormalize(project.LocalPath, out var localPath))
        {
            throw new ArgumentException("The project's local path must be absolute.", nameof(project));
        }

        if (!ClaudeProjectFolderName.TryGet(localPath, out var folderName))
        {
            throw new NotSupportedException(
                $"The project's local path is longer than Claude Code's {ClaudeProjectFolderName.MaxLength}-character storage name limit.");
        }

        return (localPath, _directory.GetProjectFolder(folderName));
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
