namespace CodeChatSync.Providers.Claude;

/// <summary>Lists only Claude transcripts in a project's private sync folder.</summary>
/// <remarks>
/// A transcript is archived under the folder its session was started in, relative to the
/// project root — <c>src/&lt;id&gt;.jsonl</c> for one run from <c>src</c> — so the archive
/// is walked at any depth. Looking only at the top level silently hid every chat started in
/// a subfolder, which is how most sessions are started.
/// </remarks>
public static class ClaudeArchivedChatDiscovery
{
    private static readonly EnumerationOptions Recursive = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Device,
        IgnoreInaccessible = true,
        MatchType = MatchType.Simple,
        MatchCasing = MatchCasing.PlatformDefault
    };

    /// <summary>
    /// Finds the archived transcript of one session, wherever under the project folder its
    /// session was started, or <see langword="null"/> when there is none.
    /// </summary>
    public static FileInfo? FindTranscript(string projectSyncFolder, string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectSyncFolder);

        // Only a well-formed session id is ever turned into a file name to search for.
        if (!Guid.TryParseExact(sessionId, "D", out _) || !Directory.Exists(projectSyncFolder))
        {
            return null;
        }

        var folder = new DirectoryInfo(Path.GetFullPath(projectSyncFolder));
        if ((folder.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("The Claude archive folder must not be a link.");
        }

        return Directory
            .EnumerateFiles(folder.FullName, sessionId + ClaudeProjectsDirectory.TranscriptExtension, Recursive)
            .Select(path => new FileInfo(path))
            .FirstOrDefault();
    }

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

        var chats = new Dictionary<string, ClaudeArchivedChat>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(folder.FullName, "*" + ClaudeProjectsDirectory.TranscriptExtension, Recursive))
        {
            var file = new FileInfo(path);
            if (!ClaudeProjectsDirectory.TryGetSessionId(file.Name, out var sessionId) || chats.ContainsKey(sessionId))
            {
                continue;
            }

            var metadata = ClaudeProjectDiscovery.TryRead(file, includeTitle: true);

            // The file's own time is only a last resort: after a pull it is the checkout time.
            var updated = metadata?.Latest ?? new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
            chats[sessionId] = new ClaudeArchivedChat(
                sessionId,
                metadata?.Title ?? metadata?.FirstPrompt,
                metadata?.Earliest,
                updated,
                file.Length);
        }

        return [.. chats.Values.OrderByDescending(chat => chat.UpdatedAt).ThenBy(chat => chat.Id, StringComparer.OrdinalIgnoreCase)];
    }
}

public sealed record ClaudeArchivedChat(
    string Id,
    string? Title,
    DateTimeOffset? CreatedAt,
    DateTimeOffset UpdatedAt,
    long Length);
