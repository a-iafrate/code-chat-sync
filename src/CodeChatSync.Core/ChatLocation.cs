namespace CodeChatSync.Core;

/// <summary>
/// A single chat file exposed by a provider, addressed by a path relative to the
/// project's folder in the sync repository.
/// </summary>
public sealed record ChatLocation
{
    /// <summary>
    /// Path relative to the project's folder in the sync repository, always using
    /// <c>/</c> as separator so it stays portable across PCs.
    /// </summary>
    public required string RelativePath { get; init; }

    /// <summary>Absolute path of the file on this PC.</summary>
    public required string LocalPath { get; init; }

    public long Length { get; init; }

    public DateTimeOffset LastWriteTimeUtc { get; init; }

    /// <summary>
    /// True when the provider reports the chat as currently open, so it must not be
    /// copied or overwritten.
    /// </summary>
    public bool IsInUse { get; init; }
}
