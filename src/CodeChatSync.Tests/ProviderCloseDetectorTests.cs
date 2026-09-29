using CodeChatSync.Core;

namespace CodeChatSync.Tests;

public sealed class ProviderCloseDetectorTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly WatchOptions Options = new() { SettleDelay = TimeSpan.FromSeconds(20) };

    [Fact]
    public void DoesNotSyncWhileTheProviderIsRunning()
    {
        var detector = new ProviderCloseDetector(Options);

        Assert.False(detector.Observe(isProviderRunning: true, Start));
        Assert.False(detector.Observe(isProviderRunning: true, Start.AddMinutes(5)));
        Assert.True(detector.IsProviderRunning);
    }

    [Fact]
    public void DoesNotSyncWhenTheProviderWasNeverSeenRunning()
    {
        var detector = new ProviderCloseDetector(Options);

        Assert.False(detector.Observe(isProviderRunning: false, Start));
        Assert.False(detector.Observe(isProviderRunning: false, Start.AddHours(1)));
        Assert.False(detector.IsWaitingToSettle);
    }

    [Fact]
    public void DoesNotSyncImmediatelyWhenTheProviderCloses()
    {
        var detector = new ProviderCloseDetector(Options);
        detector.Observe(isProviderRunning: true, Start);

        var syncedOnClose = detector.Observe(isProviderRunning: false, Start.AddSeconds(1));

        Assert.False(syncedOnClose);
        Assert.True(detector.IsWaitingToSettle);
        Assert.False(detector.IsProviderRunning);
    }

    [Fact]
    public void SyncsOnceTheSettleDelayHasElapsed()
    {
        var detector = new ProviderCloseDetector(Options);
        detector.Observe(isProviderRunning: true, Start);
        detector.Observe(isProviderRunning: false, Start.AddSeconds(1));

        Assert.False(detector.Observe(isProviderRunning: false, Start.AddSeconds(10)));
        Assert.True(detector.Observe(isProviderRunning: false, Start.AddSeconds(21)));
    }

    [Fact]
    public void SyncsOnlyOncePerClose()
    {
        var detector = new ProviderCloseDetector(Options);
        detector.Observe(isProviderRunning: true, Start);
        detector.Observe(isProviderRunning: false, Start.AddSeconds(1));
        Assert.True(detector.Observe(isProviderRunning: false, Start.AddSeconds(30)));

        Assert.False(detector.Observe(isProviderRunning: false, Start.AddSeconds(60)));
        Assert.False(detector.Observe(isProviderRunning: false, Start.AddMinutes(10)));
    }

    [Fact]
    public void RestartingTheProviderCancelsThePendingSync()
    {
        var detector = new ProviderCloseDetector(Options);
        detector.Observe(isProviderRunning: true, Start);
        detector.Observe(isProviderRunning: false, Start.AddSeconds(1));

        detector.Observe(isProviderRunning: true, Start.AddSeconds(5));

        Assert.False(detector.IsWaitingToSettle);
        Assert.False(detector.Observe(isProviderRunning: false, Start.AddSeconds(6)));
        Assert.False(detector.Observe(isProviderRunning: false, Start.AddSeconds(20)));
        Assert.True(detector.Observe(isProviderRunning: false, Start.AddSeconds(26)));
    }

    [Fact]
    public void SyncsOnCloseWhenNoSettleDelayIsWanted()
    {
        var detector = new ProviderCloseDetector(new WatchOptions { SettleDelay = TimeSpan.Zero });
        detector.Observe(isProviderRunning: true, Start);

        Assert.True(detector.Observe(isProviderRunning: false, Start.AddSeconds(1)));
    }

    [Fact]
    public void ResetDropsAPendingSync()
    {
        var detector = new ProviderCloseDetector(Options);
        detector.Observe(isProviderRunning: true, Start);
        detector.Observe(isProviderRunning: false, Start.AddSeconds(1));

        detector.Reset();

        Assert.False(detector.IsWaitingToSettle);
        Assert.False(detector.Observe(isProviderRunning: false, Start.AddSeconds(60)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RejectsANonPositivePollInterval(int seconds)
    {
        var options = new WatchOptions { PollInterval = TimeSpan.FromSeconds(seconds) };

        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }

    [Fact]
    public void RejectsANegativeSettleDelay()
    {
        var options = new WatchOptions { SettleDelay = TimeSpan.FromSeconds(-1) };

        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }
}
