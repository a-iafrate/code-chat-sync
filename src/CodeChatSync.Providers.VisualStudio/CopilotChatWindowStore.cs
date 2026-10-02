using CodeChatSync.Core;

namespace CodeChatSync.Providers.VisualStudio;

/// <summary>A chat as Visual Studio's Chat window records it, inside a solution's <c>.vs</c> folder.</summary>
/// <param name="SessionId">Shared with the agent session when the chat used the CLI agent.</param>
/// <param name="SolutionRelativePath">
/// Where the solution sits inside the project, without the <c>.vs</c> segment — for example
/// <c>src/CodeChatSync.slnx</c>. Identical on every PC that clones the repository.
/// </param>
public sealed record CopilotChatWindowEntry(string SessionId, string SolutionRelativePath, string LocalPath);

/// <summary>
/// Locates the per-solution records that decide which chats Visual Studio lists.
/// </summary>
/// <remarks>
/// <para>
/// Restoring a chat's transcript is not enough for it to appear: the Chat window lists what it
/// finds under
/// <c>&lt;solution&gt;/.vs/&lt;name&gt;/copilot-chat/&lt;workspace-id&gt;/sessions/&lt;session-id&gt;</c>.
/// Copying such a file into that folder was verified to make the chat show up, so these are
/// synced like any other chat file rather than synthesized: the record is MessagePack full of
/// Visual Studio's own types and nested binary blobs, and rebuilding one would be guesswork.
/// It is self-contained — a 36 KB record held an entire conversation — so a chat that never
/// used the CLI agent, and therefore has no transcript, still restores from this alone.
/// </para>
/// <para>
/// The <c>&lt;workspace-id&gt;</c> folder is <em>not</em> derived from the solution: the same
/// value was observed on fourteen unrelated solutions of one PC, so it identifies the machine
/// or the signed-in account. It is therefore read from disk rather than computed, since no hash
/// of the solution path, its name or the account reproduced it.
/// </para>
/// <para>
/// These files live inside the client repository, which the tool otherwise never writes to.
/// They sit in <c>.vs</c>, which Visual Studio generates and Git ignores, and there is no other
/// way to make a restored chat visible. This is a deliberate, owner-approved exception; see
/// <c>docs/ARCHITECTURE.md</c>.
/// </para>
/// </remarks>
public static class CopilotChatWindowStore
{
    /// <summary>Marks a synced path as a Chat window record rather than a transcript file.</summary>
    public const string PathSegment = ".chat-window";

    /// <summary>Stands in for a workspace folder this PC does not have yet.</summary>
    internal const string UnknownWorkspaceId = "unknown-workspace";

    private const string VisualStudioFolderName = ".vs";
    private const string ChatFolderName = "copilot-chat";
    private const string SessionsFolderName = "sessions";

    /// <summary>
    /// How deep below the project root a solution's <c>.vs</c> folder is looked for. A
    /// repository root or a single folder such as <c>src</c> are the layouts seen; scanning
    /// further would walk the whole client repository for nothing.
    /// </summary>
    private const int MaximumSolutionDepth = 3;

    /// <summary>Chat window records belonging to the project at <paramref name="projectRoot"/>.</summary>
    public static IReadOnlyList<CopilotChatWindowEntry> Discover(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);

        var entries = new List<CopilotChatWindowEntry>();
        foreach (var sessionsDirectory in EnumerateSessionDirectories(projectRoot))
        {
            var solutionRelativePath = GetSolutionRelativePath(projectRoot, sessionsDirectory);
            if (solutionRelativePath is null)
            {
                continue;
            }

            foreach (var file in SafeEnumerateFiles(sessionsDirectory))
            {
                entries.Add(new CopilotChatWindowEntry(
                    Path.GetFileName(file),
                    solutionRelativePath,
                    file));
            }
        }

        return entries;
    }

    /// <summary>
    /// This PC's workspace folder name, taken from any solution that already has one, or
    /// <see langword="null"/> when Visual Studio has never opened a chat in this project.
    /// </summary>
    public static string? FindWorkspaceId(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        return EnumerateSessionDirectories(projectRoot)
            .Select(directory => Path.GetFileName(Path.GetDirectoryName(directory)!))
            .FirstOrDefault(name => name is { Length: > 0 });
    }

    /// <summary>
    /// An existing record to copy Visual Studio's own shapes from when rebuilding one.
    /// The largest is chosen: a record of a conversation that got a reply has every shape
    /// <see cref="CopilotChatWindowRecord"/> needs, while a one-sided one does not.
    /// </summary>
    public static ReadOnlyMemory<byte>? TryFindTemplate(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);

        var largest = Discover(projectRoot)
            .Select(entry => new FileInfo(entry.LocalPath))
            .Where(file => file.Exists)
            .OrderByDescending(file => file.Length)
            .FirstOrDefault();

        if (largest is null)
        {
            return null;
        }

        try
        {
            return File.ReadAllBytes(largest.FullName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Where a rebuilt record goes: the solution of this project that Visual Studio has
    /// used most for chats. Picking the busiest one keeps restored chats beside the ones
    /// already there instead of scattering them across solutions.
    /// </summary>
    public static string? FindBusiestSessionsDirectory(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);

        return EnumerateSessionDirectories(projectRoot)
            .OrderByDescending(directory => SafeEnumerateFiles(directory).Count())
            .ThenBy(directory => directory, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    /// <summary>Sessions this project already lists, across every one of its solutions.</summary>
    public static IReadOnlyCollection<string> ListedSessionIds(string projectRoot) =>
        new HashSet<string>(
            Discover(projectRoot).Select(entry => entry.SessionId),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>Path used inside the sync folder, free of this PC's workspace folder.</summary>
    public static string BuildRelativePath(string sessionId, string solutionRelativePath) =>
        $"{sessionId}/{PathSegment}/{solutionRelativePath}";

    /// <summary>Recognizes a synced path produced by <see cref="BuildRelativePath"/>.</summary>
    public static bool TryParseRelativePath(string relativePath, out string solutionRelativePath)
    {
        solutionRelativePath = string.Empty;
        var segments = RelativePathGuard.Normalize(relativePath).Split('/');
        if (segments.Length < 3 || !string.Equals(segments[1], PathSegment, StringComparison.Ordinal))
        {
            return false;
        }

        solutionRelativePath = string.Join('/', segments[2..]);
        return solutionRelativePath.Length > 0;
    }

    /// <summary>
    /// Where a synced record belongs on this PC. When no workspace folder exists yet the path
    /// names one that cannot collide with a real chat, and the restore is refused before it is
    /// ever written.
    /// </summary>
    public static string MapToLocal(
        string projectRoot,
        string solutionRelativePath,
        string sessionId,
        string? workspaceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        var solutionDirectory = RelativePathGuard.ResolveUnder(projectRoot, solutionRelativePath);
        var solutionName = Path.GetFileName(solutionDirectory);
        var parent = Path.GetDirectoryName(solutionDirectory) ?? Path.GetFullPath(projectRoot);

        return Path.Combine(
            parent,
            VisualStudioFolderName,
            solutionName,
            ChatFolderName,
            workspaceId ?? UnknownWorkspaceId,
            SessionsFolderName,
            sessionId);
    }

    private static IEnumerable<string> EnumerateSessionDirectories(string projectRoot)
    {
        if (!Directory.Exists(projectRoot))
        {
            yield break;
        }

        var visitedDirectories = 0;
        var solutionFoldersFound = 0;
        var sessionsFoldersFound = 0;
        var stopwatch = SyncTrace.IsEnabled ? System.Diagnostics.Stopwatch.StartNew() : null;

        foreach (var solutionFolder in EnumerateSolutionFolders(projectRoot, count => visitedDirectories = count))
        {
            solutionFoldersFound++;
            var chatFolder = Path.Combine(solutionFolder, ChatFolderName);
            if (!Directory.Exists(chatFolder))
            {
                continue;
            }

            foreach (var workspaceFolder in SafeEnumerateDirectories(chatFolder))
            {
                var sessions = Path.Combine(workspaceFolder, SessionsFolderName);
                if (Directory.Exists(sessions))
                {
                    sessionsFoldersFound++;
                    yield return sessions;
                }
            }
        }

        if (stopwatch is not null)
        {
            SyncTrace.Log(
                $"CopilotChatWindowStore: walked {visitedDirectories} director{(visitedDirectories == 1 ? "y" : "ies")} under "
                + $"{projectRoot}, found {solutionFoldersFound} .vs solution folder(s) and {sessionsFoldersFound} sessions "
                + $"folder(s), in {stopwatch.Elapsed:mm\\:ss\\.fff}");
        }
    }

    /// <summary>
    /// Folder names never worth descending into while looking for a <c>.vs</c> folder:
    /// build output and restored packages can run into the thousands of entries for a
    /// solution that has been built many times, and version control metadata never holds
    /// one either. <c>.vs</c> itself and other dot-folders are excluded by the caller.
    /// </summary>
    private static readonly string[] ExcludedDirectoryNames = ["bin", "obj", "node_modules", "packages"];

    /// <summary>Every <c>.vs/&lt;solution&gt;</c> folder within reach of the project root.</summary>
    /// <param name="onFinished">Reports the total number of directories visited, for tracing.</param>
    private static IEnumerable<string> EnumerateSolutionFolders(string projectRoot, Action<int>? onFinished = null)
    {
        var pending = new Queue<(string Directory, int Depth)>();
        pending.Enqueue((Path.GetFullPath(projectRoot), 0));
        var visited = 0;

        while (pending.Count > 0)
        {
            var (directory, depth) = pending.Dequeue();
            visited++;
            var visualStudioFolder = Path.Combine(directory, VisualStudioFolderName);
            if (Directory.Exists(visualStudioFolder))
            {
                foreach (var solutionFolder in SafeEnumerateDirectories(visualStudioFolder))
                {
                    yield return solutionFolder;
                }
            }

            if (depth >= MaximumSolutionDepth)
            {
                continue;
            }

            foreach (var child in SafeEnumerateDirectories(directory))
            {
                var name = Path.GetFileName(child);
                if (!name.StartsWith('.')
                    && !ExcludedDirectoryNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    pending.Enqueue((child, depth + 1));
                }
            }
        }

        onFinished?.Invoke(visited);
    }

    private static string? GetSolutionRelativePath(string projectRoot, string sessionsDirectory)
    {
        // <project>/<any>/.vs/<solution>/copilot-chat/<workspace>/sessions
        var solutionFolder = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(sessionsDirectory)));
        var visualStudioFolder = Path.GetDirectoryName(solutionFolder);
        var parent = Path.GetDirectoryName(visualStudioFolder);
        if (solutionFolder is null || parent is null)
        {
            return null;
        }

        var relativeParent = Path.GetRelativePath(Path.GetFullPath(projectRoot), parent);
        if (relativeParent.StartsWith("..", StringComparison.Ordinal))
        {
            return null;
        }

        var solutionName = Path.GetFileName(solutionFolder);
        var combined = relativeParent is "." ? solutionName : Path.Combine(relativeParent, solutionName);
        return combined.Replace('\\', '/');
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string path)
    {
        try
        {
            return Directory.EnumerateFiles(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
