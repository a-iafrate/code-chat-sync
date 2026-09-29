namespace CodeChatSync.Providers.Claude;

/// <summary>
/// A local folder where Claude Code sessions were started, offered to the user as a
/// project to register. It is only a candidate: its identity still has to be resolved
/// from the folder's Git remote, and the user has to confirm it.
/// </summary>
public sealed record ClaudeProjectCandidate
{
    /// <summary>Working directory recorded inside the transcripts (never decoded from a folder name).</summary>
    public required string LocalPath { get; init; }

    /// <summary>True when <see cref="LocalPath"/> currently exists on this PC.</summary>
    public bool LocalPathExists { get; init; }

    /// <summary>
    /// Folder the provider reads and writes for this path, or <see langword="null"/>
    /// when the path cannot be stored (for example it is too long).
    /// </summary>
    public string? StorageFolderName { get; init; }

    /// <summary>
    /// True when another working directory maps to the same storage folder. Sync is
    /// refused for such a folder so two projects cannot be mixed.
    /// </summary>
    public bool HasStorageCollision { get; init; }

    public required IReadOnlyList<ClaudeSessionInfo> Sessions { get; init; }
}

/// <summary>Metadata of one Claude Code session; never includes message content.</summary>
public sealed record ClaudeSessionInfo
{
    public required string SessionId { get; init; }

    /// <summary>Folder under Claude Code's <c>projects</c> directory holding the transcript.</summary>
    public required string StorageFolderName { get; init; }

    /// <summary>
    /// True when the transcript lives in <see cref="ClaudeProjectCandidate.StorageFolderName"/>.
    /// Only those sessions are synced for the project.
    /// </summary>
    public bool IsInStorageFolder { get; init; }

    /// <summary>Generated title (<c>aiTitle</c>), when present.</summary>
    public string? Title { get; init; }

    public long Length { get; init; }

    public DateTimeOffset LastWriteTimeUtc { get; init; }
}
