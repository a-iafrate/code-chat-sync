namespace CodeChatSync.Providers.Claude;

/// <summary>
/// Read-only view of Claude Code's <c>projects</c> directory, restricted to the files
/// the provider is allowed to handle.
/// </summary>
/// <remarks>
/// Observed, undocumented layout:
/// <code>
/// ~/.claude/projects/
///   &lt;encoded-cwd&gt;/
///     &lt;session-id&gt;.jsonl     # transcript (the only file handled here)
///     &lt;session-id&gt;/           # per-session artifacts, e.g. subagents/ (not handled)
///     memory/                  # project memory (never handled)
/// </code>
/// Only top-level <c>&lt;guid&gt;.jsonl</c> files are exposed. Every other file or folder
/// is skipped, and symbolic links, junctions and other reparse points are never
/// followed.
/// </remarks>
internal sealed class ClaudeProjectsDirectory(string root)
{
    public const string TranscriptExtension = ".jsonl";

    public string Root { get; } = LocalPaths.TryNormalize(root, out var normalized)
        ? normalized
        : throw new ArgumentException("The Claude Code projects directory must be an absolute path.", nameof(root));

    /// <summary>
    /// Returns the storage folders, or none when the root is missing. Throws when the
    /// root or its parent is a link, since the files would then live elsewhere.
    /// </summary>
    public IEnumerable<DirectoryInfo> EnumerateProjectFolders()
    {
        var rootDirectory = GetVerifiedRoot();
        if (rootDirectory is null)
        {
            return [];
        }

        return rootDirectory.EnumerateDirectories("*", LocalPaths.TopLevelWithoutLinks);
    }

    /// <summary>Returns the storage folder for a name, verifying it is not a link.</summary>
    public DirectoryInfo GetProjectFolder(string folderName)
    {
        if (string.IsNullOrEmpty(folderName)
            || folderName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || folderName is "." or "..")
        {
            throw new ArgumentException("Invalid Claude Code project folder name.", nameof(folderName));
        }

        GetVerifiedRoot();
        var folder = new DirectoryInfo(Path.Combine(Root, folderName));
        if (LocalPaths.IsReparsePoint(folder))
        {
            throw new InvalidOperationException(
                $"The Claude Code project folder '{folderName}' is a link; linked storage is not supported.");
        }

        return folder;
    }

    /// <summary>Top-level transcripts of a storage folder, keyed by session ID.</summary>
    public static IEnumerable<(string SessionId, FileInfo File)> EnumerateTranscripts(DirectoryInfo folder)
    {
        if (!folder.Exists)
        {
            yield break;
        }

        foreach (var file in folder.EnumerateFiles("*" + TranscriptExtension, LocalPaths.TopLevelWithoutLinks))
        {
            if (TryGetSessionId(file.Name, out var sessionId))
            {
                yield return (sessionId, file);
            }
        }
    }

    /// <summary>
    /// Accepts only <c>&lt;guid&gt;.jsonl</c>: Claude Code names sessions with UUIDs, and
    /// the strict shape keeps unrelated files out.
    /// </summary>
    public static bool TryGetSessionId(string fileName, out string sessionId)
    {
        sessionId = string.Empty;
        if (!fileName.EndsWith(TranscriptExtension, StringComparison.Ordinal))
        {
            return false;
        }

        var stem = fileName[..^TranscriptExtension.Length];
        if (!Guid.TryParseExact(stem, "D", out _))
        {
            return false;
        }

        sessionId = stem;
        return true;
    }

    private DirectoryInfo? GetVerifiedRoot()
    {
        var rootDirectory = new DirectoryInfo(Root);
        if (LocalPaths.IsReparsePoint(rootDirectory)
            || (rootDirectory.Parent is { } parent && LocalPaths.IsReparsePoint(parent)))
        {
            throw new InvalidOperationException(
                "The Claude Code projects directory is a link; linked storage is not supported.");
        }

        return rootDirectory.Exists ? rootDirectory : null;
    }
}
