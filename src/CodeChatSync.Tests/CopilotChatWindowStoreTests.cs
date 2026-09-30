using CodeChatSync.Core;
using CodeChatSync.Providers.VisualStudio;
using CodeChatSync.Tests.TestSupport;

namespace CodeChatSync.Tests;

/// <summary>
/// Visual Studio lists a chat only if a record for it sits under the solution's
/// <c>.vs</c> folder, in a workspace folder whose name the tool cannot derive.
/// </summary>
public sealed class CopilotChatWindowStoreTests : IDisposable
{
    private const string WorkspaceId = "d28eb1bf";
    private const string SessionId = "43417994-9bb5-4ed7-b371-97c5e72930c0";

    private readonly TempDirectory _root = new();
    private readonly string _projectRoot;

    public CopilotChatWindowStoreTests()
    {
        _projectRoot = _root.Combine("project");
        Directory.CreateDirectory(_projectRoot);
    }

    public void Dispose() => _root.Dispose();

    [Fact]
    public void Discover_FindsARecordBesideASolutionInASubfolder()
    {
        WriteRecord("src", "CodeChatSync.slnx", WorkspaceId, SessionId);

        var entry = Assert.Single(CopilotChatWindowStore.Discover(_projectRoot));

        Assert.Equal(SessionId, entry.SessionId);
        Assert.Equal("src/CodeChatSync.slnx", entry.SolutionRelativePath);
    }

    [Fact]
    public void Discover_FindsARecordBesideASolutionAtTheProjectRoot()
    {
        WriteRecord(null, "Demo.sln", WorkspaceId, SessionId);

        var entry = Assert.Single(CopilotChatWindowStore.Discover(_projectRoot));

        Assert.Equal("Demo.sln", entry.SolutionRelativePath);
    }

    [Fact]
    public void Discover_KeepsSolutionsApart()
    {
        WriteRecord("src", "First.slnx", WorkspaceId, "11111111-1111-1111-1111-111111111111");
        WriteRecord("tools", "Second.slnx", WorkspaceId, "22222222-2222-2222-2222-222222222222");

        var entries = CopilotChatWindowStore.Discover(_projectRoot);

        Assert.Equal(
            ["src/First.slnx", "tools/Second.slnx"],
            entries.Select(entry => entry.SolutionRelativePath).Order());
    }

    [Fact]
    public void Discover_ReturnsNothingForAProjectVisualStudioNeverOpened()
    {
        Assert.Empty(CopilotChatWindowStore.Discover(_projectRoot));
    }

    /// <summary>
    /// The synced path must carry no trace of this PC: the workspace folder differs per
    /// machine, so only the solution's place in the repository is recorded.
    /// </summary>
    [Fact]
    public void BuildRelativePath_LeavesOutThisPcsWorkspaceFolder()
    {
        var relativePath = CopilotChatWindowStore.BuildRelativePath(SessionId, "src/CodeChatSync.slnx");

        Assert.Equal($"{SessionId}/.chat-window/src/CodeChatSync.slnx", relativePath);
        Assert.DoesNotContain(WorkspaceId, relativePath, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("session-1/.chat-window/src/Demo.slnx", true, "src/Demo.slnx")]
    [InlineData("session-1/.chat-window/Demo.sln", true, "Demo.sln")]
    [InlineData("session-1/events.jsonl", false, "")]
    [InlineData("session-1/checkpoints/index.md", false, "")]
    [InlineData("session-1/.chat-window", false, "")]
    public void TryParseRelativePath_TellsRecordsApartFromTranscriptFiles(
        string relativePath,
        bool expected,
        string expectedSolution)
    {
        var parsed = CopilotChatWindowStore.TryParseRelativePath(relativePath, out var solution);

        Assert.Equal(expected, parsed);
        Assert.Equal(expectedSolution, solution);
    }

    [Fact]
    public void MapToLocal_RebuildsThePathUsingThisPcsWorkspaceFolder()
    {
        WriteRecord("src", "CodeChatSync.slnx", WorkspaceId, SessionId);

        var localPath = CopilotChatWindowStore.MapToLocal(
            _projectRoot,
            "src/CodeChatSync.slnx",
            SessionId,
            CopilotChatWindowStore.FindWorkspaceId(_projectRoot));

        Assert.Equal(
            Path.Combine(_projectRoot, "src", ".vs", "CodeChatSync.slnx", "copilot-chat", WorkspaceId, "sessions", SessionId),
            localPath);
    }

    /// <summary>
    /// The workspace folder was the same for fourteen unrelated solutions of one PC, so
    /// any solution already holding chats answers for the whole project.
    /// </summary>
    [Fact]
    public void FindWorkspaceId_ReadsItFromWhicheverSolutionAlreadyHasOne()
    {
        WriteRecord("tools", "Other.slnx", WorkspaceId, SessionId);

        Assert.Equal(WorkspaceId, CopilotChatWindowStore.FindWorkspaceId(_projectRoot));
    }

    [Fact]
    public void FindWorkspaceId_ReturnsNothingBeforeVisualStudioHasCreatedOne()
    {
        Assert.Null(CopilotChatWindowStore.FindWorkspaceId(_projectRoot));
    }

    [Fact]
    public void MapToLocal_StaysInsideTheProjectWhenTheSyncedPathTriesToEscape()
    {
        Assert.Throws<ArgumentException>(() => CopilotChatWindowStore.MapToLocal(
            _projectRoot,
            "../../elsewhere",
            SessionId,
            WorkspaceId));
    }

    private void WriteRecord(string? solutionFolder, string solutionName, string workspaceId, string sessionId)
    {
        var parent = solutionFolder is null ? _projectRoot : Path.Combine(_projectRoot, solutionFolder);
        var directory = Path.Combine(parent, ".vs", solutionName, "copilot-chat", workspaceId, "sessions");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, sessionId), [0x01, 0xDE, 0x00, 0x10]);
    }
}

/// <summary>
/// The provider side of the same feature: what it reports, and when it refuses to write.
/// </summary>
public sealed class VisualStudioChatWindowProviderTests : IDisposable
{
    private readonly TempDirectory _root = new();
    private readonly string _projectRoot;
    private readonly ProjectInfo _project;

    public VisualStudioChatWindowProviderTests()
    {
        _projectRoot = _root.Combine("project");
        Directory.CreateDirectory(_projectRoot);
        _project = new ProjectInfo
        {
            Identity = ProjectIdentity.FromRemote("https://github.com/example/demo.git"),
            LocalPath = _projectRoot
        };
    }

    public void Dispose() => _root.Dispose();

    [Fact]
    public void Discover_ReportsChatWindowRecordsAlongsideTranscripts()
    {
        WriteRecord("11111111-1111-1111-1111-111111111111");
        var provider = new VisualStudioChatProvider(_root.Combine("session-state"));

        var location = Assert.Single(provider.Discover(_project));

        Assert.Equal(
            "11111111-1111-1111-1111-111111111111/.chat-window/src/Demo.slnx",
            location.RelativePath);
    }

    [Fact]
    public void GetRestoreRefusal_RefusesARecordWhenThisPcHasNoWorkspaceFolderYet()
    {
        var provider = new VisualStudioChatProvider(_root.Combine("session-state"));

        var refusal = provider.GetRestoreRefusal(
            _project,
            Path.Combine("sync", "session-1", ".chat-window", "src", "Demo.slnx"));

        Assert.NotNull(refusal);
    }

    [Fact]
    public void GetRestoreRefusal_AllowsARecordOnceVisualStudioHasOpenedAChatHere()
    {
        WriteRecord("11111111-1111-1111-1111-111111111111");
        var provider = new VisualStudioChatProvider(_root.Combine("session-state"));

        var refusal = provider.GetRestoreRefusal(
            _project,
            Path.Combine("sync", "session-1", ".chat-window", "src", "Demo.slnx"));

        Assert.Null(refusal);
    }

    [Fact]
    public void GetRestoreRefusal_NeverStandsInTheWayOfATranscriptFile()
    {
        var provider = new VisualStudioChatProvider(_root.Combine("session-state"));

        Assert.Null(provider.GetRestoreRefusal(_project, Path.Combine("sync", "session-1", "events.jsonl")));
    }

    /// <summary>A record is a binary blob of Visual Studio's own types: never rewritten.</summary>
    [Fact]
    public void IsMapped_LeavesChatWindowRecordsAlone()
    {
        var provider = new VisualStudioChatProvider(_root.Combine("session-state"));

        Assert.False(provider.IsMapped("session-1/.chat-window/src/Demo.slnx"));
    }

    private void WriteRecord(string sessionId)
    {
        var directory = Path.Combine(
            _projectRoot, "src", ".vs", "Demo.slnx", "copilot-chat", "d28eb1bf", "sessions");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, sessionId), [0x01, 0xDE, 0x00, 0x10]);
    }
}
