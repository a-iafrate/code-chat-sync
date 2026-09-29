using CodeChatSync.Core;
using CodeChatSync.Providers.VisualStudio;
using CodeChatSync.Tests.TestSupport;

namespace CodeChatSync.Tests;

public class VisualStudioChatProviderTests : IDisposable
{
    private const string Remote = "https://github.com/a-iafrate/code-chat-sync.git";

    private readonly TempDirectory _root = new();
    private readonly string _sessionStateRoot;

    public VisualStudioChatProviderTests()
    {
        _sessionStateRoot = _root.Combine("session-state");
        Directory.CreateDirectory(_sessionStateRoot);
    }

    public void Dispose() => _root.Dispose();

    [Fact]
    public void Discover_IncludesTranscriptAndDescriptor()
    {
        var project = CreateProject();
        WriteSession("session-1", Remote, project.LocalPath);

        var relativePaths = Discover(project);

        Assert.Contains("session-1/events.jsonl", relativePaths);
        Assert.Contains("session-1/workspace.yaml", relativePaths);
    }

    [Theory]
    [InlineData("session.db")]
    [InlineData("session.db-shm")]
    [InlineData("session.db-wal")]
    [InlineData("inuse.1234.lock")]
    public void Discover_ExcludesPerMachineRuntimeState(string fileName)
    {
        var project = CreateProject();
        var sessionDirectory = WriteSession("session-1", Remote, project.LocalPath);
        File.WriteAllText(Path.Combine(sessionDirectory, fileName), "runtime state");

        var relativePaths = Discover(project);

        Assert.DoesNotContain($"session-1/{fileName}", relativePaths);
    }

    [Fact]
    public void Discover_IncludesNestedSessionFiles()
    {
        var project = CreateProject();
        var sessionDirectory = WriteSession("session-1", Remote, project.LocalPath);
        Directory.CreateDirectory(Path.Combine(sessionDirectory, "checkpoints"));
        File.WriteAllText(Path.Combine(sessionDirectory, "checkpoints", "001.md"), "checkpoint");

        var relativePaths = Discover(project);

        Assert.Contains("session-1/checkpoints/001.md", relativePaths);
    }

    [Fact]
    public void Discover_MatchesSessionsByRemoteRegardlessOfLocalPath()
    {
        var project = CreateProject();
        WriteSession("session-1", "git@github.com:a-iafrate/code-chat-sync.git", @"D:\elsewhere\code-chat-sync");

        var relativePaths = Discover(project);

        Assert.Contains("session-1/events.jsonl", relativePaths);
    }

    [Fact]
    public void Discover_FallsBackToWorkingDirectoryWhenTheSessionHasNoRepository()
    {
        var project = CreateProject();
        WriteSession("session-1", repository: null, workingDirectory: project.LocalPath);

        var relativePaths = Discover(project);

        Assert.Contains("session-1/events.jsonl", relativePaths);
    }

    [Fact]
    public void Discover_IgnoresSessionsOfOtherProjects()
    {
        var project = CreateProject();
        WriteSession("session-other", "https://github.com/contoso/other-project.git", @"D:\clients\other-project");

        Assert.Empty(Discover(project));
    }

    [Fact]
    public void MapToLocal_ResolvesUnderTheSessionStateRoot()
    {
        var project = CreateProject();
        var provider = new VisualStudioChatProvider(_sessionStateRoot);

        var localPath = provider.MapToLocal(project, "session-1/events.jsonl");

        Assert.Equal(Path.Combine(_sessionStateRoot, "session-1", "events.jsonl"), localPath);
    }

    [Fact]
    public void MapToLocal_RejectsPathsEscapingTheSessionStateRoot()
    {
        var project = CreateProject();
        var provider = new VisualStudioChatProvider(_sessionStateRoot);

        Assert.Throws<ArgumentException>(() => provider.MapToLocal(project, "../escaped.jsonl"));
    }

    [Fact]
    public void ProcessNames_ReportVisualStudio()
    {
        var provider = new VisualStudioChatProvider(_sessionStateRoot);

        Assert.Equal("visualstudio", provider.Id);
        Assert.Contains("devenv", provider.ProcessNames);
    }

    [Fact]
    public void Discover_ReportsASessionAsInUseWhileItsOwningProcessRuns()
    {
        var project = CreateProject();
        var sessionDirectory = WriteSession("session-1", Remote, project.LocalPath);
        var livePid = Environment.ProcessId;
        File.WriteAllText(Path.Combine(sessionDirectory, $"inuse.{livePid}.lock"), livePid.ToString());

        var locations = new VisualStudioChatProvider(_sessionStateRoot).Discover(project).ToList();

        Assert.NotEmpty(locations);
        Assert.All(locations, location => Assert.True(location.IsInUse));
    }

    [Fact]
    public void Discover_IgnoresLocksLeftBehindByProcessesThatNoLongerExist()
    {
        var project = CreateProject();
        var sessionDirectory = WriteSession("session-1", Remote, project.LocalPath);
        File.WriteAllText(Path.Combine(sessionDirectory, $"inuse.{FindUnusedProcessId()}.lock"), "stale");

        var locations = new VisualStudioChatProvider(_sessionStateRoot).Discover(project).ToList();

        Assert.NotEmpty(locations);
        Assert.All(locations, location => Assert.False(location.IsInUse));
    }

    [Fact]
    public void Discover_TreatsAnUnreadableLockOwnerAsStillRunning()
    {
        var project = CreateProject();
        var sessionDirectory = WriteSession("session-1", Remote, project.LocalPath);
        File.WriteAllText(Path.Combine(sessionDirectory, "inuse.not-a-pid.lock"), "malformed");

        var locations = new VisualStudioChatProvider(_sessionStateRoot).Discover(project).ToList();

        Assert.NotEmpty(locations);
        Assert.All(locations, location => Assert.True(location.IsInUse));
    }

    /// <summary>Finds a process id that is not currently in use on this machine.</summary>
    private static int FindUnusedProcessId()
    {
        var live = System.Diagnostics.Process.GetProcesses().Select(process => process.Id).ToHashSet();
        for (var candidate = 999_000; candidate > 1000; candidate -= 4)
        {
            if (!live.Contains(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Unable to find an unused process id.");
    }

    private List<string> Discover(ProjectInfo project) =>
        [.. new VisualStudioChatProvider(_sessionStateRoot).Discover(project).Select(location => location.RelativePath)];

    private ProjectInfo CreateProject() => new()
    {
        Identity = ProjectIdentity.FromRemote(Remote),
        LocalPath = _root.Combine("repo")
    };

    private string WriteSession(string id, string? repository, string workingDirectory)
    {
        var sessionDirectory = Path.Combine(_sessionStateRoot, id);
        Directory.CreateDirectory(sessionDirectory);

        var descriptor = new List<string>
        {
            $"id: {id}",
            $"cwd: \"{workingDirectory.Replace("\\", "\\\\")}\"",
            "branch: main"
        };

        if (repository is not null)
        {
            descriptor.Add($"repository: {repository}");
        }

        File.WriteAllLines(Path.Combine(sessionDirectory, "workspace.yaml"), descriptor);
        File.WriteAllText(Path.Combine(sessionDirectory, "events.jsonl"), "{\"type\":\"user.message\"}\n");
        return sessionDirectory;
    }
}
