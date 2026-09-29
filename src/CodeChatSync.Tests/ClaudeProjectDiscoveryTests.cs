using CodeChatSync.Providers.Claude;
using CodeChatSync.Tests.TestSupport;

namespace CodeChatSync.Tests;

public class ClaudeProjectDiscoveryTests : IDisposable
{
    private const string SessionA = "11111111-1111-4111-8111-111111111111";
    private const string SessionB = "22222222-2222-4222-8222-222222222222";
    private const string SessionC = "33333333-3333-4333-8333-333333333333";

    private readonly ClaudeProjectsFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void DiscoverCandidates_UsesTheRecordedWorkingDirectory()
    {
        var localPath = _fixture.CreateLocalProject("client-app");
        var folder = FolderFor(localPath);
        _fixture.WriteTranscript(folder, SessionA, localPath, title: "Fix login");
        _fixture.WriteTranscript(folder, SessionB, localPath);

        var candidate = Assert.Single(Discover());

        Assert.Equal(localPath, candidate.LocalPath);
        Assert.True(candidate.LocalPathExists);
        Assert.Equal(folder, candidate.StorageFolderName);
        Assert.False(candidate.HasStorageCollision);
        Assert.Equal([SessionA, SessionB], candidate.Sessions.Select(session => session.SessionId).Order());
        Assert.All(candidate.Sessions, session => Assert.True(session.IsInStorageFolder));
        Assert.Equal("Fix login", candidate.Sessions.Single(session => session.SessionId == SessionA).Title);
        Assert.Null(candidate.Sessions.Single(session => session.SessionId == SessionB).Title);
    }

    [Fact]
    public void DiscoverCandidates_NeverDecodesFolderNames()
    {
        var localPath = _fixture.CreateLocalProject("client-app");
        _fixture.WriteTranscript("C--made-up-path", SessionA, localPath);

        var candidate = Assert.Single(Discover());

        Assert.Equal(localPath, candidate.LocalPath);
        var session = Assert.Single(candidate.Sessions);
        Assert.Equal("C--made-up-path", session.StorageFolderName);
        Assert.False(session.IsInStorageFolder);
    }

    [Fact]
    public void DiscoverCandidates_IgnoresMemoryOnlyAndUnrelatedFolders()
    {
        _fixture.WriteRaw("C--memory-only", "memory/MEMORY.md", "private memory");
        _fixture.WriteRaw("C--other-files", "notes.jsonl", "{\"cwd\":\"C:\\\\somewhere\"}");
        _fixture.WriteRaw("C--other-files", $"{SessionA}/subagents/agent-1.jsonl", "{\"cwd\":\"C:\\\\somewhere\"}");
        _fixture.WriteRaw(string.Empty, "loose-file.jsonl", "{\"cwd\":\"C:\\\\somewhere\"}");

        Assert.Empty(Discover());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative/path")]
    public void DiscoverCandidates_IgnoresTranscriptsWithoutAnAbsoluteWorkingDirectory(string? cwd)
    {
        _fixture.WriteTranscript("some-folder", SessionA, cwd);

        Assert.Empty(Discover());
    }

    [Fact]
    public void DiscoverCandidates_IgnoresMalformedTranscripts()
    {
        _fixture.WriteRaw("some-folder", $"{SessionA}.jsonl", "{\"cwd\": \n[1,2\n\u0000\u0001");

        Assert.Empty(Discover());
    }

    [Fact]
    public void DiscoverCandidates_FlagsPathsSharingAStorageFolder()
    {
        var first = _fixture.CreateLocalProject("a-b");
        var second = _fixture.CreateLocalProject("a", "b");
        var folder = FolderFor(first);
        _fixture.WriteTranscript(folder, SessionA, first);
        _fixture.WriteTranscript(folder, SessionB, second);
        _fixture.WriteTranscript(FolderFor(_fixture.CreateLocalProject("single")), SessionC, _fixture.Combine("work", "single"));

        var candidates = Discover().ToDictionary(candidate => candidate.LocalPath);

        Assert.True(candidates[first].HasStorageCollision);
        Assert.True(candidates[second].HasStorageCollision);
        Assert.False(candidates[_fixture.Combine("work", "single")].HasStorageCollision);
    }

    [Fact]
    public void DiscoverCandidates_ReportsMissingLocalFolders()
    {
        var missing = _fixture.Combine("work", "moved-away");
        _fixture.WriteTranscript(FolderFor(missing), SessionA, missing);

        var candidate = Assert.Single(Discover());

        Assert.False(candidate.LocalPathExists);
    }

    [Fact]
    public void DiscoverCandidates_NeverExposesTranscriptContent()
    {
        var localPath = _fixture.CreateLocalProject("client-app");
        _fixture.WriteTranscript(FolderFor(localPath), SessionA, localPath, title: "Title");

        var candidate = Assert.Single(Discover());

        Assert.DoesNotContain(ClaudeProjectsFixture.SecretContent, candidate.ToString());
        Assert.All(candidate.Sessions, session =>
            Assert.DoesNotContain(ClaudeProjectsFixture.SecretContent, session.ToString()));
    }

    [Fact]
    public void DiscoverCandidates_ThrowsWhileClaudeIsRunning()
    {
        var localPath = _fixture.CreateLocalProject("client-app");
        _fixture.WriteTranscript(FolderFor(localPath), SessionA, localPath);

        Assert.Throws<ClaudeCodeRunningException>(
            () => ClaudeProjectDiscovery.DiscoverCandidates(new FakeProcessGuard("claude"), _fixture.ProjectsRoot));
    }

    [Fact]
    public void DiscoverCandidates_ReturnsNothingWhenTheRootIsMissing()
    {
        Directory.Delete(_fixture.ProjectsRoot);

        Assert.Empty(Discover());
    }

    [Fact]
    public void DiscoverCandidates_SkipsLinkedFolders()
    {
        var localPath = _fixture.CreateLocalProject("client-app");
        var target = _fixture.Combine("elsewhere");
        Directory.CreateDirectory(target);
        File.WriteAllText(
            Path.Combine(target, $"{SessionA}.jsonl"),
            $"{{\"cwd\":\"{localPath.Replace("\\", "\\\\")}\"}}\n");
        if (!ClaudeProjectsFixture.TryCreateDirectoryLink(Path.Combine(_fixture.ProjectsRoot, FolderFor(localPath)), target))
        {
            return;
        }

        Assert.Empty(Discover());
    }

    [Fact]
    public void FolderName_ReplacesEveryNonAlphanumericCharacter()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        var path = Path.Combine(root, "src", "my.app_v2", "Àpi");

        Assert.True(ClaudeProjectFolderName.TryGet(path, out var folderName));

        var expectedRoot = new string([.. root.Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-')]);
        Assert.Equal(expectedRoot + "src-my-app-v2--pi", folderName);
        if (OperatingSystem.IsWindows() && root == @"C:\")
        {
            Assert.Equal("C--src-my-app-v2--pi", folderName);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative")]
    public void FolderName_RejectsPathsThatAreNotAbsolute(string? path)
    {
        Assert.False(ClaudeProjectFolderName.TryGet(path, out _));
    }

    [Fact]
    public void FolderName_RejectsNamesClaudeWouldShorten()
    {
        var path = Path.Combine(Path.GetTempPath(), new string('x', ClaudeProjectFolderName.MaxLength));

        Assert.False(ClaudeProjectFolderName.TryGet(path, out _));
    }

    private IReadOnlyList<ClaudeProjectCandidate> Discover() =>
        ClaudeProjectDiscovery.DiscoverCandidates(new FakeProcessGuard(), _fixture.ProjectsRoot);

    private static string FolderFor(string localPath) =>
        ClaudeProjectFolderName.TryGet(localPath, out var name) ? name : throw new InvalidOperationException("Path cannot be stored.");
}
