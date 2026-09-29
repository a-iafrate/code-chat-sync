using CodeChatSync.Core;
using CodeChatSync.Tests.TestSupport;

namespace CodeChatSync.Tests;

/// <summary>
/// Covers the wiring the tray app depends on: closing the provider eventually
/// produces exactly one sync, and nothing runs while it is still open.
/// </summary>
public sealed class WatchToSyncTests : IDisposable
{
    private const string Remote = "https://github.com/a-iafrate/code-chat-sync.git";

    private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly WatchOptions Options = new()
    {
        PollInterval = TimeSpan.FromSeconds(1),
        SettleDelay = TimeSpan.FromSeconds(20)
    };

    private readonly TempDirectory _root = new();

    public void Dispose() => _root.Dispose();

    [Fact]
    public async Task ClosingTheProviderSyncsTheChatsExactlyOnce()
    {
        var guard = new MutableProcessGuard();
        guard.RunningProcesses.Add("devenv");
        var time = new TestTimeProvider(Start);
        var watcher = new ProviderWatcher(guard, ["devenv"], Options, time);
        var coordinator = new SyncCoordinator(CreateOrchestrator);

        var runs = new List<SyncOutcome>();
        using var completed = new SemaphoreSlim(0);
        coordinator.Completed += (_, outcome) =>
        {
            lock (runs)
            {
                runs.Add(outcome);
            }

            completed.Release();
        };
        watcher.SyncRequested += async (_, _) => await coordinator.RunAsync();

        var chatRoot = Directory.CreateDirectory(Path.Combine(_root.Path, "chats")).FullName;
        File.WriteAllText(Path.Combine(chatRoot, "events.jsonl"), "{}");

        watcher.Poll();
        Assert.Empty(runs);

        guard.RunningProcesses.Clear();
        time.Advance(TimeSpan.FromSeconds(1));
        watcher.Poll();
        Assert.Empty(runs);

        time.Advance(TimeSpan.FromSeconds(25));
        watcher.Poll();
        Assert.True(await completed.WaitAsync(TimeSpan.FromSeconds(10)));

        time.Advance(TimeSpan.FromSeconds(60));
        watcher.Poll();
        watcher.Poll();

        var outcome = Assert.Single(runs);
        Assert.Equal(SyncOutcomeStatus.Completed, outcome.Status);
        Assert.Equal(1, outcome.Result!.PushedCount);
        Assert.True(File.Exists(Path.Combine(
            _root.Path, "sync", "fake", "a-iafrate-code-chat-sync", "events.jsonl")));
    }

    private SyncOrchestrator CreateOrchestrator()
    {
        var syncRoot = Directory.CreateDirectory(Path.Combine(_root.Path, "sync")).FullName;
        var projectPath = Directory.CreateDirectory(Path.Combine(_root.Path, "repo")).FullName;
        var chatRoot = Directory.CreateDirectory(Path.Combine(_root.Path, "chats")).FullName;

        var localConfig = new LocalConfig { SyncRootPath = syncRoot };
        localConfig.AddOrUpdate(ProjectIdentity.FromRemote(Remote), projectPath);

        return new SyncOrchestrator(
            new FakeChatProvider(chatRoot),
            new SyncWorkspace(localConfig, new SharedConfig(), Path.Combine(_root.Path, "state")),
            new ChatSyncService(new FakeProcessGuard()));
    }
}
