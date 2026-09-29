using CodeChatSync.Core;
using CodeChatSync.Tests.TestSupport;

namespace CodeChatSync.Tests;

public sealed class SyncCoordinatorTests : IDisposable
{
    private const string Remote = "https://github.com/a-iafrate/code-chat-sync.git";

    private readonly TempDirectory _root = new();

    public void Dispose() => _root.Dispose();

    [Fact]
    public async Task RunAsync_ReportsWhatWasSynced()
    {
        var coordinator = new SyncCoordinator(CreateOrchestrator);
        File.WriteAllText(Path.Combine(ChatRoot, "events.jsonl"), "{}");

        var outcome = await coordinator.RunAsync();

        Assert.Equal(SyncOutcomeStatus.Completed, outcome.Status);
        Assert.Contains("1 pushed", outcome.Summary);
        Assert.False(outcome.NeedsAttention);
        Assert.Same(outcome, coordinator.LastOutcome);
    }

    [Fact]
    public async Task RunAsync_ReportsWhenNothingIsRegistered()
    {
        var coordinator = new SyncCoordinator(() => CreateOrchestrator(registerProject: false));

        var outcome = await coordinator.RunAsync();

        Assert.Equal(SyncOutcomeStatus.Completed, outcome.Status);
        Assert.Contains("No projects are registered", outcome.Summary);
    }

    [Fact]
    public async Task RunAsync_TurnsAMissingConfigurationIntoAnActionableOutcome()
    {
        var coordinator = new SyncCoordinator(() => new SyncOrchestrator(
            new FakeChatProvider(_root.Path),
            new SyncWorkspace(new LocalConfig(), new SharedConfig(), Path.Combine(_root.Path, "state")),
            new ChatSyncService(new FakeProcessGuard())));

        var outcome = await coordinator.RunAsync();

        Assert.Equal(SyncOutcomeStatus.NotConfigured, outcome.Status);
        Assert.True(outcome.NeedsAttention);
        Assert.Contains("No sync folder", outcome.Summary);
    }

    [Fact]
    public async Task RunAsync_ReportsAnAbortedRunAsNeedingAttention()
    {
        var coordinator = new SyncCoordinator(
            () => CreateOrchestrator(publisher: new StoppingPublisher("Divergent history.")));
        File.WriteAllText(Path.Combine(ChatRoot, "events.jsonl"), "{}");

        var outcome = await coordinator.RunAsync();

        Assert.Equal(SyncOutcomeStatus.Aborted, outcome.Status);
        Assert.Equal("Divergent history.", outcome.Summary);
        Assert.True(outcome.NeedsAttention);
    }

    [Fact]
    public async Task RunAsync_DropsARequestArrivingWhileASyncIsRunning()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var coordinator = new SyncCoordinator(() =>
        {
            started.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            return CreateOrchestrator();
        });

        var first = coordinator.RunAsync();
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));

        var second = await coordinator.RunAsync();
        release.Set();
        await first;

        Assert.Equal(SyncOutcomeStatus.AlreadyRunning, second.Status);
        Assert.Equal(SyncOutcomeStatus.Completed, (await first).Status);
    }

    [Fact]
    public async Task RunAsync_RaisesCompletedOncePerRun()
    {
        var coordinator = new SyncCoordinator(CreateOrchestrator);
        var completions = 0;
        coordinator.Completed += (_, _) => Interlocked.Increment(ref completions);

        await coordinator.RunAsync();
        await coordinator.RunAsync();

        Assert.Equal(2, completions);
    }

    [Fact]
    public async Task RunAsync_IsUsableAgainAfterAFailure()
    {
        var shouldFail = true;
        var coordinator = new SyncCoordinator(() => shouldFail
            ? throw new IOException("The sync folder is locked.")
            : CreateOrchestrator());

        var failed = await coordinator.RunAsync();
        shouldFail = false;
        var recovered = await coordinator.RunAsync();

        Assert.Equal(SyncOutcomeStatus.Failed, failed.Status);
        Assert.Contains("locked", failed.Summary);
        Assert.Equal(SyncOutcomeStatus.Completed, recovered.Status);
        Assert.False(coordinator.IsRunning);
    }

    private string ChatRoot => Directory.CreateDirectory(Path.Combine(_root.Path, "chats")).FullName;

    private SyncOrchestrator CreateOrchestrator() => CreateOrchestrator(registerProject: true);

    private SyncOrchestrator CreateOrchestrator(bool registerProject = true, ISyncPublisher? publisher = null)
    {
        var syncRoot = Directory.CreateDirectory(Path.Combine(_root.Path, "sync")).FullName;
        var projectPath = Directory.CreateDirectory(Path.Combine(_root.Path, "repo")).FullName;

        var localConfig = new LocalConfig { SyncRootPath = syncRoot };
        if (registerProject)
        {
            localConfig.AddOrUpdate(ProjectIdentity.FromRemote(Remote), projectPath);
        }

        return new SyncOrchestrator(
            new FakeChatProvider(ChatRoot),
            new SyncWorkspace(localConfig, new SharedConfig(), Path.Combine(_root.Path, "state")),
            new ChatSyncService(new FakeProcessGuard()),
            publisher);
    }

    private sealed class StoppingPublisher(string reason) : ISyncPublisher
    {
        public SyncPublishResult PrepareForSync() => SyncPublishResult.Stop(reason);

        public SyncPublishResult PublishChanges(int pushedCount) => SyncPublishResult.Ok();
    }
}
