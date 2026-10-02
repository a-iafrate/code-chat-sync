using CodeChatSync.Core;
using CodeChatSync.Tests.TestSupport;

namespace CodeChatSync.Tests;

/// <summary>
/// A dry run inspects; it copies nothing. Treating it like a sync made the window answer
/// its own notification: finishing a run told the window to refresh, refreshing ran a dry
/// run, and that dry run announced itself as finished. The gate was then busy for good
/// and every request came back "a sync is already running".
/// </summary>
public sealed class SyncCoordinatorDryRunTests : IDisposable
{
    private const string Remote = "https://github.com/example/logged.git";

    private readonly TempDirectory _root = new();
    private readonly SyncCoordinator _coordinator;
    private int _runs;

    public SyncCoordinatorDryRunTests()
    {
        var chatRoot = Directory.CreateDirectory(_root.Combine("chats")).FullName;
        File.WriteAllText(Path.Combine(chatRoot, "session-1.jsonl"), "a chat");

        _coordinator = new SyncCoordinator(() =>
        {
            _runs++;
            return new SyncOrchestrator(
                new FakeChatProvider(chatRoot),
                CreateWorkspace("sync", "state"),
                new ChatSyncService(new FakeProcessGuard()));
        });
    }

    private SyncWorkspace CreateWorkspace(string syncFolder, string stateFolder)
    {
        var syncRoot = Directory.CreateDirectory(_root.Combine(syncFolder)).FullName;
        var projectPath = Directory.CreateDirectory(_root.Combine("repo")).FullName;
        var localConfig = new LocalConfig { SyncRootPath = syncRoot };
        localConfig.AddOrUpdate(ProjectIdentity.FromRemote(Remote), projectPath);
        return new SyncWorkspace(localConfig, new SharedConfig(), _root.Combine(stateFolder));
    }

    public void Dispose() => _root.Dispose();

    [Fact]
    public async Task RunAsync_DoesNotAnnounceADryRun()
    {
        var announced = 0;
        _coordinator.Completed += (_, _) => announced++;

        await _coordinator.RunAsync(new SyncRunOptions { DryRun = true });

        Assert.Equal(0, announced);
        Assert.Equal(1, _runs);
    }

    /// <summary>
    /// The shape that looped: a listener that inspects whenever a run completes must not
    /// be able to trigger itself.
    /// </summary>
    [Fact]
    public async Task RunAsync_AListenerThatInspectsOnCompletionDoesNotLoop()
    {
        // The window's shape: every completion triggers a refresh, and refreshing inspects.
        var announcements = 0;
        var inspected = new TaskCompletionSource();
        _coordinator.Completed += async (_, _) =>
        {
            announcements++;
            await _coordinator.RunAsync(new SyncRunOptions { DryRun = true });
            inspected.TrySetResult();
        };

        await _coordinator.RunAsync();
        await inspected.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Announced once. Before the fix the inspection announced itself in turn, and the
        // count grew until the gate stayed shut.
        Assert.Equal(1, announcements);
    }

    [Fact]
    public async Task RunAsync_LeavesTheLastRealResultInPlaceAfterADryRun()
    {
        await _coordinator.RunAsync();
        var afterSync = _coordinator.LastOutcome;

        await _coordinator.RunAsync(new SyncRunOptions { DryRun = true });

        Assert.Same(afterSync, _coordinator.LastOutcome);
    }

    [Fact]
    public async Task RunAsync_ReleasesTheGateSoTheNextRunCanStart()
    {
        await _coordinator.RunAsync();
        await _coordinator.RunAsync(new SyncRunOptions { DryRun = true });

        var outcome = await _coordinator.RunAsync();

        Assert.NotEqual(SyncOutcomeStatus.AlreadyRunning, outcome.Status);
        Assert.False(_coordinator.IsRunning);
    }

    [Fact]
    public async Task RecentRuns_KeepsFinishedSyncsNewestFirstAndSkipsDryRuns()
    {
        await _coordinator.RunAsync();
        await _coordinator.RunAsync(new SyncRunOptions { DryRun = true });
        await _coordinator.RunAsync();

        var log = _coordinator.RecentRuns;

        Assert.Equal(2, log.Count);
        Assert.True(log[0].At >= log[1].At);
        Assert.All(log, entry => Assert.Equal(SyncOutcomeStatus.Completed, entry.Status));
    }

    [Fact]
    public async Task RecentRuns_ExplainsWhyAFileWasNotCopied()
    {
        var chatRoot = Directory.CreateDirectory(_root.Combine("busy-chats")).FullName;
        File.WriteAllText(Path.Combine(chatRoot, "session-9.jsonl"), "being written right now");

        var provider = new FakeChatProvider(chatRoot, "devenv");
        provider.InUseRelativePaths.Add("session-9.jsonl");

        var coordinator = new SyncCoordinator(() => new SyncOrchestrator(
            provider,
            CreateWorkspace("busy-sync", "busy-state"),
            new ChatSyncService(new FakeProcessGuard())));

        await coordinator.RunAsync();

        var entry = Assert.Single(coordinator.RecentRuns);
        Assert.NotNull(entry.Detail);
        Assert.Contains("session-9.jsonl", entry.Detail!, StringComparison.Ordinal);
        Assert.Contains("currently open", entry.Detail!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RecentRuns_RecordsWhyARunCouldNotStart()
    {
        var coordinator = new SyncCoordinator(() => throw new SyncConfigurationException("No sync folder is configured."));

        await coordinator.RunAsync();

        var entry = Assert.Single(coordinator.RecentRuns);
        Assert.Equal(SyncOutcomeStatus.NotConfigured, entry.Status);
        Assert.Contains("No sync folder is configured.", entry.Detail!, StringComparison.Ordinal);
        Assert.True(entry.NeedsAttention);
    }

    [Fact]
    public async Task Started_IsRaisedForARealSyncOnly()
    {
        var starts = 0;
        _coordinator.Started += (_, _) => starts++;

        await _coordinator.RunAsync();
        await _coordinator.RunAsync(new SyncRunOptions { DryRun = true });

        Assert.Equal(1, starts);
    }
}
