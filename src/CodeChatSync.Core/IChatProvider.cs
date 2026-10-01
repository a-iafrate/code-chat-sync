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

/// <summary>
/// Optional provider capability for local files the provider generated itself from data
/// that is already synced.
/// </summary>
/// <remarks>
/// Such a file is not a copy of anything another PC owns: it was derived here and can be
/// derived again. Publishing it would store the same content twice, and because two PCs
/// deriving it independently rarely produce identical bytes, it would be reported as a
/// conflict on content nobody wrote. It therefore gives way without argument to a copy
/// arriving from another PC, which by definition is the real thing.
/// </remarks>
public interface IDerivedChatContent
{
    /// <summary>
    /// Whether the local file behind <paramref name="relativePath"/> is still one this
    /// provider generated. False once the tool itself has rewritten it, at which point it
    /// is ordinary chat data and must be synced like any other.
    /// </summary>
    bool IsDerivedLocally(ProjectInfo project, string relativePath);
}

/// <summary>Optional provider capability for per-session restore selection.</summary>
public interface IChatSessionProvider : IChatProvider
{
    /// <summary>Returns the stable session ID for a file in the project's sync folder.</summary>
    string GetSessionId(string relativePath);
}

/// <summary>
/// Optional provider capability for tools that keep their own index of chats besides
/// the chat files, and only list a chat that appears in it.
/// </summary>
/// <remarks>
/// Copying a chat's files is then not enough to make it visible: the restored session
/// also has to be announced to the tool. The Core does not know what such an index
/// looks like, so it only says which sessions this PC is allowed to see and leaves the
/// rest to the provider, which must be able to run repeatedly without duplicating
/// anything.
/// </remarks>
public interface IChatSessionRegistrar : IChatSessionProvider
{
    /// <summary>
    /// Makes every session in <paramref name="sessionIds"/> that is present locally
    /// visible to the tool, skipping those it already knows about.
    /// </summary>
    /// <param name="dryRun">Report what would be registered without writing anything.</param>
    SessionRegistrationResult RegisterSessions(
        ProjectInfo project,
        IReadOnlyCollection<string> sessionIds,
        bool dryRun);
}
