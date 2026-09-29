namespace CodeChatSync.Core;

/// <summary>
/// A project registered on this PC: a stable identity plus the path it currently
/// occupies on this machine.
/// </summary>
public sealed record ProjectInfo
{
    /// <summary>Identity derived from the Git remote; stable across PCs.</summary>
    public required ProjectIdentity Identity { get; init; }

    /// <summary>Absolute path of the project on this PC.</summary>
    public required string LocalPath { get; init; }

    /// <summary>Display name, defaulting to the identity slug when not configured.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Folder name used for this project inside the sync repository.</summary>
    public string SyncFolderName => DisplayName is { Length: > 0 } name ? name : Identity.Slug;
}
