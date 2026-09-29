using CodeChatSync.Core;

namespace CodeChatSync.Git;

/// <summary>Applies settings for this PC's private sync repository.</summary>
public static class SyncRepositorySettings
{
    public static void Save(string folder, string? originUrl, bool initialize, string? configPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var fullPath = Path.GetFullPath(folder.Trim());
        var config = LocalConfig.Load(configPath);

        foreach (var project in config.Projects)
        {
            if (IsSameOrChild(project.LocalPath, fullPath)
                || IsSameOrChild(fullPath, project.LocalPath))
            {
                throw new ArgumentException("The sync folder must not overlap a registered client project.", nameof(folder));
            }
        }

        var parentRepository = GitRemoteReader.FindRepositoryRoot(fullPath);
        if (parentRepository is not null && !Path.GetFullPath(parentRepository).Equals(
                fullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The sync folder must not be inside another Git repository.", nameof(folder));
        }

        if (!Directory.Exists(fullPath) && !initialize)
        {
            throw new DirectoryNotFoundException($"Sync folder not found: {fullPath}. Select an existing folder or initialize a new repository.");
        }

        var repository = new SyncRepository(fullPath);
        if (initialize)
        {
            repository.Initialize();
        }

        if (!string.IsNullOrWhiteSpace(originUrl))
        {
            repository.SetOriginRemoteUrl(originUrl);
        }

        config.SyncRootPath = fullPath;
        config.Save(configPath);
    }

    private static bool IsSameOrChild(string parent, string candidate)
    {
        var relative = Path.GetRelativePath(parent, candidate);
        return relative == "." || (!Path.IsPathRooted(relative)
            && relative != ".."
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }
}
