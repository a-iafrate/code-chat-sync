using System.Globalization;

namespace CodeChatSync.Providers.VisualStudio;

/// <summary>
/// Read-only discovery of Copilot chat sessions stored by Visual Studio.
/// </summary>
/// <remarks>
/// Visual Studio keeps chat sessions outside the solution, under
/// <c>%LOCALAPPDATA%\Microsoft\VisualStudio\CopilotCli\session-state\&lt;id&gt;</c>.
/// Each session folder holds a <c>workspace.yaml</c> descriptor and an
/// <c>events.jsonl</c> transcript. None of this is documented by Microsoft, so
/// discovery is best-effort and never assumes the layout is stable.
/// </remarks>
public static class CopilotChatDiscovery
{
    internal const string WorkspaceDescriptorFileName = "workspace.yaml";
    private const string TranscriptFileName = "events.jsonl";

    /// <summary>Default session-state root for the current user.</summary>
    public static string GetDefaultSessionStateRoot()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "Microsoft", "VisualStudio", "CopilotCli", "session-state");
    }

    /// <summary>Lists every session found under <paramref name="sessionStateRoot"/>.</summary>
    public static IReadOnlyList<CopilotChatSession> DiscoverSessions(string? sessionStateRoot = null)
    {
        var root = sessionStateRoot ?? GetDefaultSessionStateRoot();
        if (!Directory.Exists(root))
        {
            return [];
        }

        var sessions = new List<CopilotChatSession>();
        foreach (var sessionDirectory in Directory.EnumerateDirectories(root))
        {
            if ((File.GetAttributes(sessionDirectory) & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            var descriptor = Path.Combine(sessionDirectory, WorkspaceDescriptorFileName);
            if (File.Exists(descriptor)
                && (File.GetAttributes(descriptor) & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            var session = TryReadSession(sessionDirectory);
            if (session is not null)
            {
                sessions.Add(session);
            }
        }

        return sessions
            .OrderByDescending(session => session.UpdatedAt ?? DateTimeOffset.MinValue)
            .ToArray();
    }

    /// <summary>
    /// Lists sessions whose recorded working directory is <paramref name="directory"/>
    /// or a folder beneath it.
    /// </summary>
    public static IReadOnlyList<CopilotChatSession> DiscoverSessionsForDirectory(
        string directory,
        string? sessionStateRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var target = NormalizeDirectory(Path.GetFullPath(directory));
        return DiscoverSessions(sessionStateRoot)
            .Where(session => IsWithin(session.WorkingDirectory, target))
            .ToArray();
    }

    private static CopilotChatSession? TryReadSession(string sessionDirectory)
    {
        var descriptorPath = Path.Combine(sessionDirectory, WorkspaceDescriptorFileName);
        if (!File.Exists(descriptorPath))
        {
            return null;
        }

        var descriptor = TryReadDescriptor(descriptorPath);
        if (descriptor is null)
        {
            return null;
        }

        descriptor.TryGetValue("id", out var id);
        descriptor.TryGetValue("cwd", out var workingDirectory);
        descriptor.TryGetValue("name", out var name);
        descriptor.TryGetValue("repository", out var repository);
        descriptor.TryGetValue("git_root", out var gitRoot);
        descriptor.TryGetValue("branch", out var branch);
        descriptor.TryGetValue("host_type", out var hostType);
        descriptor.TryGetValue("client_name", out var clientName);

        return new CopilotChatSession
        {
            Id = string.IsNullOrWhiteSpace(id) ? Path.GetFileName(sessionDirectory) : id,
            SessionDirectory = sessionDirectory,
            WorkingDirectory = Normalize(workingDirectory),
            Repository = Normalize(repository),
            GitRoot = Normalize(gitRoot),
            Branch = Normalize(branch),
            HostType = Normalize(hostType),
            ClientName = Normalize(clientName),
            Name = Normalize(name),
            IsUserNamed = ParseBoolean(descriptor, "user_named"),
            CreatedAt = ParseTimestamp(descriptor, "created_at"),
            UpdatedAt = ParseTimestamp(descriptor, "updated_at"),
            IsInUse = HasLockFile(sessionDirectory),
            Files = ListFiles(sessionDirectory)
        };
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// Reads the flat <c>key: value</c> pairs of a session descriptor.
    /// </summary>
    /// <remarks>
    /// The descriptor is simple enough that a line reader avoids taking a YAML
    /// dependency. Nested structures are ignored rather than guessed at.
    /// </remarks>
    private static Dictionary<string, string>? TryReadDescriptor(string descriptorPath)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(descriptorPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            if (line.Length == 0 || char.IsWhiteSpace(line[0]) || line.StartsWith('#'))
            {
                continue;
            }

            var separatorIndex = line.IndexOf(':');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            var value = Unquote(line[(separatorIndex + 1)..].Trim());
            if (key.Length != 0)
            {
                values[key] = value;
            }
        }

        return values;
    }

    /// <summary>
    /// Unwraps a double-quoted scalar and decodes the escapes the descriptor uses.
    /// </summary>
    private static string Unquote(string value)
    {
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"')
        {
            return value;
        }

        var inner = value[1..^1];
        var builder = new System.Text.StringBuilder(inner.Length);
        for (var index = 0; index < inner.Length; index++)
        {
            if (inner[index] != '\\' || index + 1 >= inner.Length)
            {
                builder.Append(inner[index]);
                continue;
            }

            index++;
            builder.Append(inner[index] switch
            {
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                '"' => '"',
                '\\' => '\\',
                var other => other
            });
        }

        return builder.ToString();
    }

    private static bool? ParseBoolean(IReadOnlyDictionary<string, string> descriptor, string key)
    {
        if (!descriptor.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return bool.TryParse(raw.Trim(), out var parsed) ? parsed : null;
    }

    private static DateTimeOffset? ParseTimestamp(IReadOnlyDictionary<string, string> descriptor, string key)
    {
        if (!descriptor.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            raw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// Whether a session is currently open, based on its <c>inuse.&lt;pid&gt;.lock</c> files.
    /// </summary>
    /// <remarks>
    /// A lock is only honored while the process that wrote it is still alive: Visual
    /// Studio leaves the file behind when it exits unexpectedly, and stale locks were
    /// observed months old. Treating those as "open" would silently exclude the
    /// session from syncing forever. A lock whose owner cannot be determined is
    /// treated as live, which errs on the side of not touching the files.
    /// </remarks>
    private static bool HasLockFile(string sessionDirectory)
    {
        try
        {
            foreach (var lockFile in Directory.EnumerateFiles(sessionDirectory, "inuse.*.lock"))
            {
                if (IsLockOwnerRunning(lockFile))
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsLockOwnerRunning(string lockFilePath)
    {
        var segments = Path.GetFileName(lockFilePath).Split('.');
        if (segments.Length != 3 || !int.TryParse(segments[1], CultureInfo.InvariantCulture, out var processId))
        {
            return true;
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            // No process with that id: the lock was left behind.
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static IReadOnlyList<CopilotChatSessionFile> ListFiles(string sessionDirectory)
    {
        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true
        };

        try
        {
            return Directory.EnumerateFiles(sessionDirectory, "*", enumerationOptions)
                .Select(path => new FileInfo(path))
                .Select(file => new CopilotChatSessionFile(
                    Path.GetRelativePath(sessionDirectory, file.FullName),
                    file.Length,
                    file.LastWriteTimeUtc))
                .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Relative path of a session's transcript, for callers that need it.</summary>
    public static string GetTranscriptRelativePath() => TranscriptFileName;

    private static bool IsWithin(string? candidate, string normalizedTarget)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        string normalizedCandidate;
        try
        {
            normalizedCandidate = NormalizeDirectory(Path.GetFullPath(candidate));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (string.Equals(normalizedCandidate, normalizedTarget, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = normalizedTarget + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDirectory(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
