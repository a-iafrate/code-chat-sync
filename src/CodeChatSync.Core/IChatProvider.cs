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
