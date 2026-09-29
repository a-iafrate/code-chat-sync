namespace CodeChatSync.Providers.VisualStudio;

/// <summary>
/// A Copilot chat session stored by Visual Studio.
/// </summary>
/// <remarks>
/// The on-disk format is undocumented and may change between Visual Studio
/// versions, so every value here is best-effort.
/// </remarks>
public sealed record CopilotChatSession
{
    public required string Id { get; init; }

    /// <summary>Folder holding the session's state files.</summary>
    public required string SessionDirectory { get; init; }

    /// <summary>Working directory recorded for the session, when present.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Repository slug recorded for the session, for example <c>owner/name</c>.</summary>
    public string? Repository { get; init; }

    /// <summary>Repository root recorded for the session.</summary>
    public string? GitRoot { get; init; }

    public string? Branch { get; init; }

    /// <summary>Repository host recorded for the session, for example <c>github</c>.</summary>
    public string? HostType { get; init; }

    /// <summary>Client that produced the session, for example <c>microsoft/visualstudio-chat</c>.</summary>
    public string? ClientName { get; init; }

    /// <summary>Title shown in Visual Studio's chat history, when present.</summary>
    public string? Name { get; init; }

    /// <summary>True when the title was set by the user rather than generated.</summary>
    public bool? IsUserNamed { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>True when a lock file suggests the session is currently open.</summary>
    public bool IsInUse { get; init; }

    /// <summary>Relative paths and sizes of the session's files.</summary>
    public required IReadOnlyList<CopilotChatSessionFile> Files { get; init; }
}

/// <summary>A single file belonging to a chat session.</summary>
public sealed record CopilotChatSessionFile(string RelativePath, long Length, DateTimeOffset LastWriteTime);
