using CodeChatSync.Core;
using CodeChatSync.Providers.Claude;
using CodeChatSync.Tests.TestSupport;

namespace CodeChatSync.Tests;

public class ClaudeCodeChatProviderTests : IDisposable
{
    private const string SessionA = "11111111-1111-4111-8111-111111111111";
    private const string SessionB = "22222222-2222-4222-8222-222222222222";

    private readonly ClaudeProjectsFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void Identity_UsesStableIdAndClaudeProcess()
    {
        var provider = CreateProvider();

        Assert.Equal("claudecode", provider.Id);
        Assert.Equal(["claude"], provider.ProcessNames);
    }

    [Fact]
    public void Discover_ReturnsTranscriptsRecordedForTheConfirmedPath()
    {
        var localPath = _fixture.CreateLocalProject("client-app");
        var transcript = _fixture.WriteTranscript(FolderFor(localPath), SessionA, localPath);

        var locations = CreateProvider().Discover(ClaudeProjectsFixture.Project(localPath)).ToList();

        var location = Assert.Single(locations);
        Assert.Equal($"{SessionA}.jsonl", location.RelativePath);
        Assert.Equal(transcript, location.LocalPath);
        Assert.Equal(new FileInfo(transcript).Length, location.Length);
        Assert.False(location.IsInUse);
    }

    [Fact]
    public void Discover_SkipsEverythingButTopLevelSessionTranscripts()
    {
        var localPath = _fixture.CreateLocalProject("client-app");
        var folder = FolderFor(localPath);
        _fixture.WriteTranscript(folder, SessionA, localPath);
        _fixture.WriteRaw(folder, "memory/MEMORY.md", "private memory");
        _fixture.WriteRaw(folder, $"{SessionA}/subagents/agent-1.jsonl", $"{{\"cwd\":\"{Escape(localPath)}\"}}");
        _fixture.WriteRaw(folder, "notes.jsonl", $"{{\"cwd\":\"{Escape(localPath)}\"}}");
        _fixture.WriteRaw(folder, $"{SessionB}.json", $"{{\"cwd\":\"{Escape(localPath)}\"}}");
        _fixture.WriteRaw(folder, $"{SessionB}.jsonl.bak", $"{{\"cwd\":\"{Escape(localPath)}\"}}");

        var relativePaths = Discover(localPath);

        Assert.Equal([$"{SessionA}.jsonl"], relativePaths);
    }

    [Fact]
    public void Discover_IgnoresTranscriptsWithoutAMatchingWorkingDirectory()
    {
        var localPath = _fixture.CreateLocalProject("client-app");
        var folder = FolderFor(localPath);
        _fixture.WriteTranscript(folder, SessionA, cwd: null);
        _fixture.WriteTranscript(folder, SessionB, _fixture.CreateLocalProject("other"));

        Assert.Empty(Discover(localPath));
    }

    [Fact]
    public void Discover_DoesNotUseTranscriptsStoredUnderAnotherFolder()
    {
        var localPath = _fixture.CreateLocalProject("client-app");
        _fixture.WriteTranscript("unrelated-folder", SessionA, localPath);

        Assert.Empty(Discover(localPath));
    }

    [Fact]
    public void Discover_ReturnsNothingWhenStorageIsMissing()
    {
        var localPath = _fixture.CreateLocalProject("client-app");
        Directory.Delete(_fixture.ProjectsRoot);

        Assert.Empty(Discover(localPath));
    }

    [Fact]
    public void Discover_ThrowsWhileClaudeIsRunning()
    {
        var localPath = _fixture.CreateLocalProject("client-app");
        _fixture.WriteTranscript(FolderFor(localPath), SessionA, localPath);
        var provider = new ClaudeCodeChatProvider(new FakeProcessGuard("claude"), _fixture.ProjectsRoot);

        var exception = Assert.Throws<ClaudeCodeRunningException>(
            () => provider.Discover(ClaudeProjectsFixture.Project(localPath)));
        Assert.Equal(["claude"], exception.RunningProcesses);
    }

    [Fact]
    public void Discover_IgnoresOtherProvidersProcesses()
    {
        var localPath = _fixture.CreateLocalProject("client-app");
        _fixture.WriteTranscript(FolderFor(localPath), SessionA, localPath);
        var provider = new ClaudeCodeChatProvider(new FakeProcessGuard("devenv"), _fixture.ProjectsRoot);

        Assert.Single(provider.Discover(ClaudeProjectsFixture.Project(localPath)));
    }

    [Fact]
    public void Discover_RejectsARelativeLocalPath()
    {
        Assert.Throws<ArgumentException>(() => Discover(Path.Combine("relative", "client-app")));
    }

    [Fact]
    public void Discover_RejectsATooLongLocalPath()
    {
        var localPath = _fixture.Combine("work", new string('x', ClaudeProjectFolderName.MaxLength));

        Assert.Throws<NotSupportedException>(() => Discover(localPath));
    }

    [Fact]
    public void MapToLocal_RemapsToTheStorageFolderOfThisPcsPath()
    {
        // The transcript was produced on another PC where the project lived elsewhere.
        var localPath = _fixture.CreateLocalProject("clients", "app");

        var mapped = CreateProvider().MapToLocal(ClaudeProjectsFixture.Project(localPath), $"{SessionA}.jsonl");

        Assert.Equal(Path.Combine(_fixture.ProjectsRoot, FolderFor(localPath), $"{SessionA}.jsonl"), mapped);
        Assert.False(File.Exists(mapped));
        Assert.False(Directory.Exists(Path.GetDirectoryName(mapped)));
    }

    [Fact]
    public void MapToLocal_AcceptsTranscriptsRestoredFromAnotherPc()
    {
        var localPath = _fixture.CreateLocalProject("client-app");
        var folder = FolderFor(localPath);
        _fixture.WriteTranscript(folder, SessionA, _fixture.Combine("other-pc", "elsewhere", "client-app"));

        var mapped = CreateProvider().MapToLocal(ClaudeProjectsFixture.Project(localPath), $"{SessionB}.jsonl");

        Assert.Equal(Path.Combine(_fixture.ProjectsRoot, folder, $"{SessionB}.jsonl"), mapped);
    }

    [Fact]
    public void MapToLocal_RefusesAFolderSharedWithAnotherLocalPath()
    {
        // "a-b" and "a/b" produce the same Claude Code folder name.
        var localPath = _fixture.CreateLocalProject("a-b");
        var collidingPath = _fixture.CreateLocalProject("a", "b");
        Assert.Equal(FolderFor(localPath), FolderFor(collidingPath));
        _fixture.WriteTranscript(FolderFor(localPath), SessionA, collidingPath);

        var provider = CreateProvider();

        Assert.Throws<InvalidOperationException>(
            () => provider.MapToLocal(ClaudeProjectsFixture.Project(localPath), $"{SessionB}.jsonl"));
        Assert.Empty(provider.Discover(ClaudeProjectsFixture.Project(localPath)));
    }

    [Fact]
    public void MapToLocal_RefusesAMissingLocalFolder()
    {
        var localPath = _fixture.Combine("work", "not-cloned-here");

        Assert.Throws<InvalidOperationException>(
            () => CreateProvider().MapToLocal(ClaudeProjectsFixture.Project(localPath), $"{SessionA}.jsonl"));
    }

    [Fact]
    public void MapToLocal_ThrowsWhileClaudeIsRunning()
    {
        var localPath = _fixture.CreateLocalProject("client-app");
        var provider = new ClaudeCodeChatProvider(new FakeProcessGuard("CLAUDE"), _fixture.ProjectsRoot);

        Assert.Throws<ClaudeCodeRunningException>(
            () => provider.MapToLocal(ClaudeProjectsFixture.Project(localPath), $"{SessionA}.jsonl"));
    }

    [Theory]
    [InlineData("11111111-1111-4111-8111-111111111111/subagents/agent-1.jsonl")]
    [InlineData("memory/MEMORY.md")]
    [InlineData("notes.jsonl")]
    [InlineData("11111111-1111-4111-8111-111111111111.json")]
    [InlineData("../11111111-1111-4111-8111-111111111111.jsonl")]
    [InlineData("C:/11111111-1111-4111-8111-111111111111.jsonl")]
    [InlineData("")]
    public void MapToLocal_RejectsPathsOtherThanATranscript(string relativePath)
    {
        var localPath = _fixture.CreateLocalProject("client-app");

        Assert.ThrowsAny<ArgumentException>(
            () => CreateProvider().MapToLocal(ClaudeProjectsFixture.Project(localPath), relativePath));
    }

    [Fact]
    public void GetSessionId_ReturnsTheTranscriptStem()
    {
        Assert.Equal(SessionA, CreateProvider().GetSessionId($"{SessionA}.jsonl"));
    }

    [Theory]
    [InlineData("11111111-1111-4111-8111-111111111111/subagents/agent-1.jsonl")]
    [InlineData("not-a-guid.jsonl")]
    [InlineData("../11111111-1111-4111-8111-111111111111.jsonl")]
    public void GetSessionId_RejectsOtherPaths(string relativePath)
    {
        Assert.ThrowsAny<ArgumentException>(() => CreateProvider().GetSessionId(relativePath));
    }

    [Fact]
    public void Constructor_RejectsARelativeProjectsRoot()
    {
        Assert.Throws<ArgumentException>(
            () => new ClaudeCodeChatProvider(new FakeProcessGuard(), Path.Combine("relative", "projects")));
    }

    [Fact]
    public void LinkedProjectsRoot_IsRefused()
    {
        var target = _fixture.Combine("real-projects");
        Directory.CreateDirectory(target);
        var linkedRoot = _fixture.Combine("linked-claude", "projects");
        Directory.CreateDirectory(Path.GetDirectoryName(linkedRoot)!);
        if (!ClaudeProjectsFixture.TryCreateDirectoryLink(linkedRoot, target))
        {
            return;
        }

        var localPath = _fixture.CreateLocalProject("client-app");
        var provider = new ClaudeCodeChatProvider(new FakeProcessGuard(), linkedRoot);

        Assert.Throws<InvalidOperationException>(() => provider.Discover(ClaudeProjectsFixture.Project(localPath)));
        Assert.Throws<InvalidOperationException>(
            () => provider.MapToLocal(ClaudeProjectsFixture.Project(localPath), $"{SessionA}.jsonl"));
    }

    [Fact]
    public void LinkedStorageFolder_IsRefused()
    {
        var localPath = _fixture.CreateLocalProject("client-app");
        var target = _fixture.Combine("elsewhere");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, $"{SessionA}.jsonl"), $"{{\"cwd\":\"{Escape(localPath)}\"}}\n");
        if (!ClaudeProjectsFixture.TryCreateDirectoryLink(Path.Combine(_fixture.ProjectsRoot, FolderFor(localPath)), target))
        {
            return;
        }

        var provider = CreateProvider();

        Assert.Throws<InvalidOperationException>(() => provider.Discover(ClaudeProjectsFixture.Project(localPath)));
        Assert.Throws<InvalidOperationException>(
            () => provider.MapToLocal(ClaudeProjectsFixture.Project(localPath), $"{SessionA}.jsonl"));
    }

    [Fact]
    public void LinkedTranscript_IsSkippedAndNotWritable()
    {
        var localPath = _fixture.CreateLocalProject("client-app");
        var target = _fixture.WriteRaw("outside", "target.jsonl", $"{{\"cwd\":\"{Escape(localPath)}\"}}\n");
        var link = Path.Combine(_fixture.ProjectsRoot, FolderFor(localPath), $"{SessionA}.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        if (!ClaudeProjectsFixture.TryCreateFileLink(link, target))
        {
            // Creating file symbolic links needs Developer Mode or elevation on Windows.
            return;
        }

        var provider = CreateProvider();

        Assert.Empty(provider.Discover(ClaudeProjectsFixture.Project(localPath)));
        Assert.Throws<InvalidOperationException>(
            () => provider.MapToLocal(ClaudeProjectsFixture.Project(localPath), $"{SessionA}.jsonl"));
    }

    [Fact]
    public void Sync_ArchivesTranscriptButRefusesRestoreAcrossDifferentProjectPaths()
    {
        var syncRoot = _fixture.Combine("sync");
        Directory.CreateDirectory(syncRoot);
        var service = new ChatSyncService(new FakeProcessGuard());

        var pathA = _fixture.CreateLocalProject("pc-a", "client-app");
        var rootA = _fixture.ProjectsRoot;
        var transcript = _fixture.WriteTranscript(FolderFor(pathA), SessionA, pathA, title: "Title");
        var pushed = service.Sync(new ClaudeCodeChatProvider(new FakeProcessGuard(), rootA), ClaudeProjectsFixture.Project(pathA), syncRoot, new SyncState());

        var pathB = _fixture.CreateLocalProject("pc-b", "d", "client-app");
        var rootB = _fixture.Combine("pc-b-claude", "projects");
        var pulled = service.Sync(new ClaudeCodeChatProvider(new FakeProcessGuard(), rootB), ClaudeProjectsFixture.Project(pathB), syncRoot, new SyncState());

        Assert.Equal(1, pushed.PushedCount);
        Assert.Equal(0, pulled.PulledCount);
        var skipped = Assert.Single(pulled.Entries);
        Assert.Equal(SyncAction.Skipped, skipped.Action);
        Assert.Contains("cwd", skipped.Reason, StringComparison.OrdinalIgnoreCase);
        var restored = Path.Combine(rootB, FolderFor(pathB), $"{SessionA}.jsonl");
        Assert.False(File.Exists(restored));
        var syncFolder = ClaudeProjectsFixture.Project(pathA).SyncFolderName;
        Assert.Equal(File.ReadAllText(transcript), File.ReadAllText(Path.Combine(syncRoot, "claudecode", syncFolder, $"{SessionA}.jsonl")));
    }

    private ClaudeCodeChatProvider CreateProvider() =>
        new(new FakeProcessGuard(), _fixture.ProjectsRoot);

    private List<string> Discover(string localPath) =>
        [.. CreateProvider().Discover(ClaudeProjectsFixture.Project(localPath)).Select(location => location.RelativePath)];

    private static string FolderFor(string localPath) =>
        ClaudeProjectFolderName.TryGet(localPath, out var name) ? name : throw new InvalidOperationException("Path cannot be stored.");

    private static string Escape(string path) => path.Replace("\\", "\\\\");
}
