namespace CodeChatSync.Git;

/// <summary>A remote configured in a Git repository.</summary>
public sealed record GitRemote(string Name, string Url);

/// <summary>
/// Reads remotes straight from a repository's <c>.git/config</c>.
/// </summary>
/// <remarks>
/// Project identity only needs the remote URL, so this avoids both a Git library
/// dependency and a dependency on the <c>git</c> executable being installed.
/// </remarks>
public static class GitRemoteReader
{
    private const string OriginRemoteName = "origin";

    /// <summary>
    /// Walks up from <paramref name="startPath"/> to the repository root, or returns
    /// <see langword="null"/> when the path is not inside a Git repository.
    /// </summary>
    public static string? FindRepositoryRoot(string startPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startPath);

        var directory = Directory.Exists(startPath)
            ? new DirectoryInfo(Path.GetFullPath(startPath))
            : new FileInfo(Path.GetFullPath(startPath)).Directory;

        while (directory is not null)
        {
            var gitPath = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>Lists the remotes configured for the repository containing a path.</summary>
    public static IReadOnlyList<GitRemote> ReadRemotes(string repositoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);

        var root = FindRepositoryRoot(repositoryPath);
        if (root is null)
        {
            return [];
        }

        var configPath = ResolveConfigPath(root);
        if (configPath is null || !File.Exists(configPath))
        {
            return [];
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(configPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        return ParseRemotes(lines);
    }

    /// <summary>
    /// Returns the remote used to identify the project: <c>origin</c> when present,
    /// otherwise the only remote configured. Returns <see langword="null"/> when the
    /// choice would be ambiguous, so the caller can ask instead of guessing.
    /// </summary>
    public static string? FindPrimaryRemoteUrl(string repositoryPath)
    {
        var remotes = ReadRemotes(repositoryPath);

        var origin = remotes.FirstOrDefault(remote =>
            string.Equals(remote.Name, OriginRemoteName, StringComparison.OrdinalIgnoreCase));

        if (origin is not null)
        {
            return origin.Url;
        }

        return remotes.Count == 1 ? remotes[0].Url : null;
    }

    /// <summary>
    /// Resolves the config file, following the <c>gitdir:</c> indirection used by
    /// worktrees and submodules, where <c>.git</c> is a file rather than a folder.
    /// </summary>
    private static string? ResolveConfigPath(string repositoryRoot)
    {
        var gitPath = Path.Combine(repositoryRoot, ".git");

        if (Directory.Exists(gitPath))
        {
            return Path.Combine(gitPath, "config");
        }

        string pointer;
        try
        {
            pointer = File.ReadAllText(gitPath).Trim();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        const string prefix = "gitdir:";
        if (!pointer.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var gitDirectory = pointer[prefix.Length..].Trim();
        if (gitDirectory.Length == 0)
        {
            return null;
        }

        if (!Path.IsPathRooted(gitDirectory))
        {
            gitDirectory = Path.GetFullPath(Path.Combine(repositoryRoot, gitDirectory));
        }

        // A worktree's own git directory points at the shared one, which holds the config.
        var commonDirectoryFile = Path.Combine(gitDirectory, "commondir");
        if (File.Exists(commonDirectoryFile))
        {
            try
            {
                var commonDirectory = File.ReadAllText(commonDirectoryFile).Trim();
                if (commonDirectory.Length > 0)
                {
                    gitDirectory = Path.IsPathRooted(commonDirectory)
                        ? commonDirectory
                        : Path.GetFullPath(Path.Combine(gitDirectory, commonDirectory));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Fall through and try the config next to the worktree git directory.
            }
        }

        return Path.Combine(gitDirectory, "config");
    }

    private static List<GitRemote> ParseRemotes(IEnumerable<string> lines)
    {
        var remotes = new List<GitRemote>();
        string? currentRemote = null;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                currentRemote = ParseRemoteSectionName(line[1..^1].Trim());
                continue;
            }

            if (currentRemote is null)
            {
                continue;
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            if (!string.Equals(key, "url", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var url = line[(separatorIndex + 1)..].Trim();
            if (url.Length > 0 && !remotes.Any(remote =>
                    string.Equals(remote.Name, currentRemote, StringComparison.OrdinalIgnoreCase)))
            {
                remotes.Add(new GitRemote(currentRemote, url));
            }
        }

        return remotes;
    }

    /// <summary>Extracts the remote name from a <c>[remote "name"]</c> section header.</summary>
    private static string? ParseRemoteSectionName(string section)
    {
        const string sectionPrefix = "remote";
        if (!section.StartsWith(sectionPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var remainder = section[sectionPrefix.Length..].Trim();
        if (remainder.Length < 2 || remainder[0] != '"' || remainder[^1] != '"')
        {
            return null;
        }

        var name = remainder[1..^1].Trim();
        return name.Length == 0 ? null : name;
    }
}
