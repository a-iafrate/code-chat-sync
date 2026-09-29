namespace CodeChatSync.Core;

/// <summary>
/// Validates provider-supplied relative paths before they are combined with a root
/// directory, so a malformed path cannot escape the intended folder.
/// </summary>
public static class RelativePathGuard
{
    /// <summary>
    /// Returns the path normalized to <c>/</c> separators, or throws when it is
    /// rooted or walks outside its root.
    /// </summary>
    public static string Normalize(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        var unified = relativePath.Replace('\\', '/').Trim('/');
        if (unified.Length == 0)
        {
            throw new ArgumentException("Relative path must not be empty.", nameof(relativePath));
        }

        if (Path.IsPathRooted(relativePath) || unified.Contains(':'))
        {
            throw new ArgumentException($"Relative path must not be rooted: '{relativePath}'.", nameof(relativePath));
        }

        var segments = unified.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment == ".."))
        {
            throw new ArgumentException($"Relative path must not traverse upwards: '{relativePath}'.", nameof(relativePath));
        }

        return string.Join('/', segments.Where(segment => segment != "."));
    }

    /// <summary>Combines a root with a validated relative path.</summary>
    public static string ResolveUnder(string root, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        var normalized = Normalize(relativePath);
        var fullRoot = Path.GetFullPath(root);
        var combined = Path.GetFullPath(Path.Combine(fullRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));

        var prefix = fullRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Relative path resolves outside its root: '{relativePath}'.", nameof(relativePath));
        }

        return combined;
    }
}
