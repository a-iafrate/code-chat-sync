namespace CodeChatSync.Core;

/// <summary>
/// Contract every AI tool integration implements. The Core owns copying, comparison,
/// backup and Git operations; a provider only says where chats live.
/// </summary>
public interface IChatProvider
{
    /// <summary>Stable provider key, used as a folder name in the sync repository.</summary>
    string Id { get; }

    /// <summary>
    /// Process names indicating the tool is in use, without extension (for example
    /// <c>devenv</c>). Used for the write lock and for the watch trigger.
    /// </summary>
    IReadOnlyList<string> ProcessNames { get; }

    /// <summary>Lists the chat files belonging to <paramref name="project"/> on this PC.</summary>
    IEnumerable<ChatLocation> Discover(ProjectInfo project);

    /// <summary>
    /// Maps a path relative to the project's sync folder back to an absolute local
    /// path on this PC, for restore.
    /// </summary>
    string MapToLocal(ProjectInfo project, string relativePath);
}

/// <summary>Optional provider check before an archived chat is copied to local storage.</summary>
public interface IChatRestoreValidator
{
    /// <summary>Returns a reason to refuse the restore, or null when it is safe to copy.</summary>
    string? GetRestoreRefusal(ProjectInfo project, string archivedPath);
}

/// <summary>
/// Optional provider capability for chat files that embed PC-specific data, such as
/// absolute paths, which must be rewritten when a chat moves between PCs.
/// </summary>
/// <remarks>
/// The sync folder always holds the portable form. Comparison and baselines use the
/// portable form too, so a file rewritten for this PC is not mistaken for a local edit.
/// Both conversions must return the input unchanged when there is nothing to map.
/// </remarks>
public interface IChatContentMapper
{
    /// <summary>Whether the file at <paramref name="relativePath"/> needs mapping.</summary>
    bool IsMapped(string relativePath);

    /// <summary>Converts this PC's copy into the form stored in the sync folder.</summary>
    byte[] ToPortable(ProjectInfo project, byte[] localContent);

    /// <summary>Converts the sync folder's form into the content this PC's tool expects.</summary>
    byte[] ToLocal(ProjectInfo project, byte[] portableContent);
}

/// <summary>Optional provider capability for per-session restore selection.</summary>
public interface IChatSessionProvider : IChatProvider
{
    /// <summary>Returns the stable session ID for a file in the project's sync folder.</summary>
    string GetSessionId(string relativePath);
}
