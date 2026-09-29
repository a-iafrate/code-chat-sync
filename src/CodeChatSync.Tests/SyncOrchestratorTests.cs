using CodeChatSync.Core;
using CodeChatSync.Tests.TestSupport;

namespace CodeChatSync.Tests;

public sealed class SyncOrchestratorTests : IDisposable
{
    private const string Remote = "https://github.com/a-iafrate/code-chat-sync.git";

    private readonly TempDirectory _root = new();

    public void Dispose() => _root.Dispose();

    [Fact]
    public void Run_CopiesChatsAndPublishesThem()
    {
        var context = CreateContext();
        File.WriteAllText(Path.Combine(context.ChatRoot, "events.jsonl"), "{}");
        var publisher = new RecordingPublisher();

        var result = CreateOrchestrator(context, publisher).Run();

        Assert.False(result.Aborted);
        Assert.Equal(1, result.PushedCount);
        Assert.True(publisher.Prepared);
        Assert.Equal(1, publisher.PublishedPushedCount);
        Assert.True(File.Exists(Path.Combine(context.SyncRoot, "fake", "a-iafrate-code-chat-sync", "events.jsonl")));
    }

    [Fact]
    public void Run_StopsWithoutTouchingFilesWhenTheRepositoryCannotBePrepared()
    {
        var context = CreateContext();
        File.WriteAllText(Path.Combine(context.ChatRoot, "events.jsonl"), "{}");
        var publisher = new RecordingPublisher { PrepareResult = SyncPublishResult.Stop("Divergent history.") };

        var result = CreateOrchestrator(context, publisher).Run();

        Assert.True(result.Aborted);
        Assert.Equal("Divergent history.", result.AbortReason);
        Assert.Empty(result.Projects);
        Assert.Null(publisher.PublishedPushedCount);
        Assert.False(Directory.Exists(Path.Combine(context.SyncRoot, "fake")));
    }

    [Fact]
    public void Run_DoesNotPublishOnADryRun()
    {
        var context = CreateContext();
        File.WriteAllText(Path.Combine(context.ChatRoot, "events.jsonl"), "{}");
        var publisher = new RecordingPublisher();

        var result = CreateOrchestrator(context, publisher).Run(new SyncRunOptions { DryRun = true });

        Assert.False(publisher.Prepared);
        Assert.Null(publisher.PublishedPushedCount);
        Assert.Equal(1, result.PushedCount);
        Assert.False(File.Exists(Path.Combine(context.SyncRoot, "fake", "a-iafrate-code-chat-sync", "events.jsonl")));
    }

    [Fact]
    public void Run_PersistsBaselinesSoASecondRunReportsNoChanges()
    {
        var context = CreateContext();
        File.WriteAllText(Path.Combine(context.ChatRoot, "events.jsonl"), "{}");

        Assert.Equal(1, CreateOrchestrator(context).Run().PushedCount);
        var second = CreateOrchestrator(context).Run();

        Assert.Equal(0, second.PushedCount);
        Assert.Equal(1, second.Projects.Single().Report.UnchangedCount);
    }

    [Fact]
    public void Run_DoesNotPersistBaselinesOnADryRun()
    {
        var context = CreateContext();
        File.WriteAllText(Path.Combine(context.ChatRoot, "events.jsonl"), "{}");

        CreateOrchestrator(context).Run(new SyncRunOptions { DryRun = true });
        var second = CreateOrchestrator(context).Run(new SyncRunOptions { DryRun = true });

        Assert.Equal(1, second.PushedCount);
    }

    [Fact]
    public void Run_SkipsProjectsThatDoNotMatchTheFilter()
    {
        var context = CreateContext();
        File.WriteAllText(Path.Combine(context.ChatRoot, "events.jsonl"), "{}");

        var matched = CreateOrchestrator(context).Run(new SyncRunOptions { ProjectFilter = "code-chat-sync" });
        var unmatched = CreateOrchestrator(context).Run(new SyncRunOptions { ProjectFilter = "other-client" });

        Assert.Single(matched.Projects);
        Assert.Empty(unmatched.Projects);
    }

    [Fact]
    public void Run_DoesNotPrepareTheRepositoryWhenNoProjectIsRegistered()
    {
        var context = CreateContext(registerProject: false);
        var publisher = new RecordingPublisher();

        var result = CreateOrchestrator(context, publisher).Run();

        Assert.Empty(result.Projects);
        Assert.False(publisher.Prepared);
    }

    [Fact]
    public void Run_ReportsProjectsWhoseLocalPathIsGone()
    {
        var context = CreateContext();
        var localConfig = context.LocalConfig;
        localConfig.AddOrUpdate(ProjectIdentity.FromRemote("https://github.com/acme/missing.git"), Path.Combine(_root.Path, "gone"));

        var result = CreateOrchestrator(context).Run();

        var unresolved = Assert.Single(result.Unresolved);
        Assert.Contains("does not exist", unresolved.Reason);
    }

    [Fact]
    public void Run_FailsWhenNoSyncFolderIsConfigured()
    {
        var workspace = new SyncWorkspace(new LocalConfig(), new SharedConfig(), Path.Combine(_root.Path, "state"));
        var orchestrator = new SyncOrchestrator(
            new FakeChatProvider(_root.Path),
            workspace,
            new ChatSyncService(new FakeProcessGuard()));

        var exception = Assert.Throws<SyncConfigurationException>(() => orchestrator.Run());

        Assert.Contains("No sync folder", exception.Message);
    }

    [Fact]
    public void Run_FailsWhenTheSyncFolderNoLongerExists()
    {
        var localConfig = new LocalConfig { SyncRootPath = Path.Combine(_root.Path, "gone") };
        var workspace = new SyncWorkspace(localConfig, new SharedConfig(), Path.Combine(_root.Path, "state"));
        var orchestrator = new SyncOrchestrator(
            new FakeChatProvider(_root.Path),
            workspace,
            new ChatSyncService(new FakeProcessGuard()));

        var exception = Assert.Throws<SyncConfigurationException>(() => orchestrator.Run());

        Assert.Contains("no longer exists", exception.Message);
    }

    private Context CreateContext(bool registerProject = true)
    {
        var syncRoot = Directory.CreateDirectory(Path.Combine(_root.Path, "sync")).FullName;
        var projectPath = Directory.CreateDirectory(Path.Combine(_root.Path, "repo")).FullName;
        var chatRoot = Directory.CreateDirectory(Path.Combine(_root.Path, "chats")).FullName;

        var localConfig = new LocalConfig { SyncRootPath = syncRoot };
        if (registerProject)
        {
            localConfig.AddOrUpdate(ProjectIdentity.FromRemote(Remote), projectPath);
        }

        return new Context(syncRoot, chatRoot, localConfig, Path.Combine(_root.Path, "state"));
    }

    private static SyncOrchestrator CreateOrchestrator(Context context, ISyncPublisher? publisher = null) =>
        new(
            new FakeChatProvider(context.ChatRoot),
            new SyncWorkspace(context.LocalConfig, new SharedConfig(), context.StateDirectory),
            new ChatSyncService(new FakeProcessGuard()),
            publisher);

    private sealed record Context(string SyncRoot, string ChatRoot, LocalConfig LocalConfig, string StateDirectory);

    /// <summary>Records how the orchestrator drove publishing, without using Git.</summary>
    private sealed class RecordingPublisher : ISyncPublisher
    {
        public SyncPublishResult PrepareResult { get; init; } = SyncPublishResult.Ok("Pulled.");

        public bool Prepared { get; private set; }

        public int? PublishedPushedCount { get; private set; }

        public SyncPublishResult PrepareForSync()
        {
            Prepared = true;
            return PrepareResult;
        }

        public SyncPublishResult PublishChanges(int pushedCount)
        {
            PublishedPushedCount = pushedCount;
            return SyncPublishResult.Ok("Published.");
        }
    }
}
