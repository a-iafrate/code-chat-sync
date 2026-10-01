namespace CodeChatSync.Providers.Claude;

/// <summary>Path normalization and file-system safety checks shared by the provider.</summary>
internal static class LocalPaths
{
    /// <summary>Case sensitivity of paths on the current platform's default file system.</summary>
    public static StringComparison Comparison { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    /// <summary>
    /// Returns the full path without a trailing separator. Paths that are not fully
    /// qualified on this platform (for example a Linux path read on Windows) are refused
    /// rather than resolved against the current directory.
    /// </summary>
    public static bool TryNormalize(string? path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0') || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        var root = Path.GetPathRoot(fullPath);
        normalized = string.Equals(fullPath, root, StringComparison.Ordinal)
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return true;
    }

    public static bool AreSame(string left, string right) => string.Equals(left, right, Comparison);

    /// <summary>
    /// True when <paramref name="candidate"/> is <paramref name="root"/> or sits below it.
    /// </summary>
    /// <remarks>
    /// Claude Code stores a session under a folder derived from the directory it was
    /// started in, which is often a subfolder of the project rather than its root.
    /// </remarks>
    public static bool IsWithin(string candidate, string root)
    {
        if (AreSame(candidate, root))
        {
            return true;
        }

        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, Comparison);
    }

    /// <summary>
    /// Where <paramref name="candidate"/> sits inside <paramref name="root"/>, with forward
    /// slashes and empty for the root itself, so the sync folder holds the same path on
    /// every PC.
    /// </summary>
    public static string GetRelativeDirectory(string candidate, string root)
    {
        if (AreSame(candidate, root))
        {
            return string.Empty;
        }

        return Path.GetRelativePath(root, candidate).Replace('\\', '/').Trim('/');
    }

    /// <summary>True when the entry exists and is a symbolic link, junction or other reparse point.</summary>
    public static bool IsReparsePoint(FileSystemInfo entry)
    {
        entry.Refresh();
        return entry.Exists && entry.Attributes.HasFlag(FileAttributes.ReparsePoint);
    }

    /// <summary>Top-level entries only; links are never followed.</summary>
    public static EnumerationOptions TopLevelWithoutLinks { get; } = new()
    {
        RecurseSubdirectories = false,
        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Device,
        IgnoreInaccessible = true,
        MatchType = MatchType.Simple,
        MatchCasing = MatchCasing.PlatformDefault
    };
}
