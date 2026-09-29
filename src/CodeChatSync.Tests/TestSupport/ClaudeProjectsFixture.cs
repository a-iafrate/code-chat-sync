using System.Diagnostics;
using System.Text.Json;
using CodeChatSync.Core;

namespace CodeChatSync.Tests.TestSupport;

/// <summary>
/// Builds a synthetic Claude Code <c>projects</c> directory in a temp folder. Never
/// touches the real <c>~/.claude</c>.
/// </summary>
internal sealed class ClaudeProjectsFixture : IDisposable
{
    /// <summary>Marker placed in message content, to prove it never leaks into reports.</summary>
    public const string SecretContent = "SECRET-TRANSCRIPT-CONTENT";

    private readonly TempDirectory _temp = new();

    public ClaudeProjectsFixture()
    {
        ProjectsRoot = _temp.Combine(".claude", "projects");
        Directory.CreateDirectory(ProjectsRoot);
    }

    public string ProjectsRoot { get; }

    public string Combine(params string[] segments) => _temp.Combine(segments);

    /// <summary>Creates a local project folder and returns its absolute path.</summary>
    public string CreateLocalProject(params string[] segments)
    {
        var path = _temp.Combine(["work", .. segments]);
        Directory.CreateDirectory(path);
        return path;
    }

    public static ProjectInfo Project(string localPath) => new()
    {
        Identity = ProjectIdentity.FromRemote("https://github.com/example/client-app.git"),
        LocalPath = localPath
    };

    /// <summary>Writes a transcript with a few records, returning its full path.</summary>
    public string WriteTranscript(string folderName, string sessionId, string? cwd, string? title = null)
    {
        var records = new List<string>
        {
            "not json at all",
            JsonSerializer.Serialize(new { type = "summary", summary = SecretContent })
        };

        if (cwd is not null)
        {
            records.Add(JsonSerializer.Serialize(new
            {
                type = "user",
                sessionId,
                cwd,
                message = new { role = "user", content = SecretContent, cwd = "nested-values-are-ignored" }
            }));
        }

        records.Add(JsonSerializer.Serialize(new { type = "assistant", sessionId, message = new { content = SecretContent } }));
        if (title is not null)
        {
            records.Add(JsonSerializer.Serialize(new { type = "ai-title", sessionId, aiTitle = "Old title" }));
            records.Add(JsonSerializer.Serialize(new { type = "ai-title", sessionId, aiTitle = title }));
        }

        return WriteRaw(folderName, sessionId + ".jsonl", string.Join('\n', records) + "\n");
    }

    public string WriteRaw(string folderName, string relativePath, string content)
    {
        var fullPath = Path.Combine(ProjectsRoot, folderName, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        return fullPath;
    }

    /// <summary>
    /// Creates a directory link: a symbolic link when permitted, otherwise a Windows
    /// junction. Returns <see langword="false"/> when neither can be created.
    /// </summary>
    public static bool TryCreateDirectoryLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            ArgumentList = { "/c", "mklink", "/J", linkPath, targetPath },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        process.WaitForExit();
        return process.ExitCode == 0 && Directory.Exists(linkPath);
    }

    public static bool TryCreateFileLink(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        // Remove links first so the recursive delete never walks into a link target.
        try
        {
            RemoveLinks(new DirectoryInfo(_temp.Path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        _temp.Dispose();
    }

    private static void RemoveLinks(DirectoryInfo directory)
    {
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                entry.Delete();
            }
            else if (entry is DirectoryInfo child)
            {
                RemoveLinks(child);
            }
        }
    }
}
