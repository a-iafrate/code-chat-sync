using CodeChatSync.Core;

namespace CodeChatSync.Providers.VisualStudio;

/// <summary>
/// Exposes Visual Studio's Copilot chat sessions to the Core.
/// </summary>
/// <remarks>
/// Sessions live outside the solution, under
/// <c>%LOCALAPPDATA%\Microsoft\VisualStudio\CopilotCli\session-state\&lt;session-id&gt;</c>.
/// The layout is undocumented, so everything here is best-effort and stays inside
/// this project.
/// </remarks>
public sealed class VisualStudioChatProvider(string? sessionStateRoot = null) : IChatSessionProvider
{
    /// <summary>
    /// Runtime state that is specific to one PC or to a live session, and therefore
    /// must never be copied between machines.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The transcript lives entirely in <c>events.jsonl</c>; the descriptor lives in
    /// <c>workspace.yaml</c>. Both are synced.
    /// </para>
    /// <para>
    /// A session's <c>session.db</c> is excluded: it is the agent's scratch database
    /// (todo and inbox tables), it is absent from most sessions, and the richest
    /// transcripts observed had no such file at all. Losing it does not lose chat
    /// content.
    /// </para>
    /// <para>
    /// The machine-wide <c>session-store.db</c> one level above the session folders
    /// is never a candidate for syncing: it indexes the sessions of <em>every</em>
    /// repository on the PC, so copying it into a sync repository would mix
    /// unrelated projects' data. Any future need to register a restored session
    /// there must insert only that session's rows locally.
    /// </para>
    /// </remarks>
    private static readonly string[] ExcludedExtensions =
    [
        ".lock",
        ".db",
        ".db-shm",
        ".db-wal"
    ];

    private readonly string _sessionStateRoot = sessionStateRoot
        ?? CopilotChatDiscovery.GetDefaultSessionStateRoot();

    public string Id => "visualstudio";

    public IReadOnlyList<string> ProcessNames { get; } = ["devenv"];

    /// <summary>Root under which this PC stores Copilot chat sessions.</summary>
    public string SessionStateRoot => _sessionStateRoot;

    public IEnumerable<ChatLocation> Discover(ProjectInfo project)
    {
        ArgumentNullException.ThrowIfNull(project);

        foreach (var session in CopilotChatDiscovery.DiscoverSessions(_sessionStateRoot))
        {
            if (!BelongsToProject(session, project))
            {
                continue;
            }

            foreach (var file in session.Files)
            {
                if (IsExcluded(file.RelativePath))
                {
                    continue;
                }

                var relativePath = $"{session.Id}/{file.RelativePath.Replace('\\', '/')}";
                yield return new ChatLocation
                {
                    RelativePath = relativePath,
                    LocalPath = Path.Combine(session.SessionDirectory, file.RelativePath),
                    Length = file.Length,
                    LastWriteTimeUtc = file.LastWriteTime,
                    IsInUse = session.IsInUse
                };
            }
        }
    }

    public string MapToLocal(ProjectInfo project, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(project);
        return RelativePathGuard.ResolveUnder(_sessionStateRoot, relativePath);
    }

    public string GetSessionId(string relativePath)
    {
        var normalized = RelativePathGuard.Normalize(relativePath);
        var separator = normalized.IndexOf('/');
        if (separator <= 0 || separator == normalized.Length - 1)
        {
            throw new ArgumentException("Expected a session ID followed by a file path.", nameof(relativePath));
        }

        return normalized[..separator];
    }

    /// <summary>
    /// Matches a session to a project by Git remote first, falling back to the
    /// recorded working directory when the session has no repository.
    /// </summary>
    private static bool BelongsToProject(CopilotChatSession session, ProjectInfo project)
    {
        if (ProjectIdentity.TryFromRemote(session.Repository, out var sessionIdentity)
            && sessionIdentity is not null)
        {
            if (string.Equals(
                    sessionIdentity.NormalizedRemote,
                    project.Identity.NormalizedRemote,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return IsWithin(session.GitRoot ?? session.WorkingDirectory, project.LocalPath);
    }

    private static bool IsExcluded(string relativePath)
    {
        var fileName = Path.GetFileName(relativePath);
        return ExcludedExtensions.Any(extension =>
            fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsWithin(string? candidate, string root)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        string fullCandidate;
        string fullRoot;
        try
        {
            fullCandidate = Trim(Path.GetFullPath(candidate));
            fullRoot = Trim(Path.GetFullPath(root));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (string.Equals(fullCandidate, fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return fullCandidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string Trim(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
