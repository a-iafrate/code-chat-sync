namespace CodeChatSync.Providers.Claude;

/// <summary>Lists only Claude transcripts in a project's private sync folder.</summary>
public static class ClaudeArchivedChatDiscovery
{
    public static IReadOnlyList<ClaudeArchivedChat> Discover(string projectSyncFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectSyncFolder);
        var folder = new DirectoryInfo(Path.GetFullPath(projectSyncFolder));
        if (!folder.Exists)
        {
            return [];
        }

        if ((folder.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("The Claude archive folder must not be a link.");
        }

        return [.. ClaudeProjectsDirectory.EnumerateTranscripts(folder)
            .Select(entry => new ClaudeArchivedChat(
                entry.SessionId,
                ClaudeProjectDiscovery.TryRead(entry.File, includeTitle: true)?.Title,
                entry.File.LastWriteTimeUtc))
            .OrderByDescending(entry => entry.UpdatedAt)];
    }
}

public sealed record ClaudeArchivedChat(string Id, string? Title, DateTime UpdatedAt);
