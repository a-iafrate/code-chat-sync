using CodeChatSync.Core;
using CodeChatSync.Tests.TestSupport;

namespace CodeChatSync.Tests;

public sealed class ProviderWatcherTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly WatchOptions Options = new()
    {
        PollInterval = TimeSpan.FromSeconds(1),
        SettleDelay = TimeSpan.FromSeconds(20)
    };

    [Fact]
    public void RequestsASyncOnlyAfterTheProviderClosesAndSettles()
    {
        var guard = new MutableProcessGuard();
        guard.RunningProcesses.Add("devenv");
        var time = new TestTimeProvider(Start);
        var watcher = new ProviderWatcher(guard, ["devenv"], Options, time);
        var requests = 0;
        watcher.SyncRequested += (_, _) => requests++;

        watcher.Poll();
        Assert.True(watcher.IsProviderRunning);
        Assert.Equal(0, requests);

        guard.RunningProcesses.Clear();
        time.Advance(TimeSpan.FromSeconds(1));
        watcher.Poll();
        Assert.False(watcher.IsProviderRunning);
        Assert.Equal(0, requests);

        time.Advance(TimeSpan.FromSeconds(25));
        watcher.Poll();
        Assert.Equal(1, requests);
    }

    [Fact]
    public void ReportsProviderStartAndStopOnlyOnChange()
    {
        var guard = new MutableProcessGuard();
        var time = new TestTimeProvider(Start);
        var watcher = new ProviderWatcher(guard, ["devenv"], Options, time);
        var changes = new List<bool>();
        watcher.ProviderRunningChanged += (_, running) => changes.Add(running);

        watcher.Poll();
        guard.RunningProcesses.Add("devenv");
        watcher.Poll();
        watcher.Poll();
        guard.RunningProcesses.Clear();
        watcher.Poll();
        watcher.Poll();

        Assert.Equal([true, false], changes);
    }

    [Fact]
    public void IgnoresUnrelatedProcesses()
    {
        var guard = new MutableProcessGuard();
        guard.RunningProcesses.Add("notepad");
        var watcher = new ProviderWatcher(guard, ["devenv"], Options, new TestTimeProvider(Start));

        watcher.Poll();

        Assert.False(watcher.IsProviderRunning);
    }

    [Fact]
    public async Task WatchAsync_PollsUntilCancelled()
    {
        var guard = new MutableProcessGuard();
        var watcher = new ProviderWatcher(
            guard,
            ["devenv"],
            new WatchOptions { PollInterval = TimeSpan.FromMilliseconds(10), SettleDelay = TimeSpan.Zero });

        using var cancellation = new CancellationTokenSource();
        var watching = watcher.WatchAsync(cancellation.Token);

        while (guard.QueryCount < 3)
        {
            await Task.Delay(10);
        }

        await cancellation.CancelAsync();
        await watching;

        Assert.True(guard.QueryCount >= 3);
        Assert.True(watching.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task WatchAsync_StopsWithoutThrowingWhenCancelledBeforeTheFirstTick()
    {
        var watcher = new ProviderWatcher(new MutableProcessGuard(), ["devenv"], Options);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await watcher.WatchAsync(cancellation.Token);
    }
}
