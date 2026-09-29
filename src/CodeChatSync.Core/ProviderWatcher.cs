namespace CodeChatSync.Core;

/// <summary>Why the watch asked for a sync.</summary>
public enum WatchTriggerReason
{
    /// <summary>The provider closed and its files have settled.</summary>
    ProviderClosed
}

/// <summary>
/// Watches the provider's processes and raises a sync request once they have been
/// closed long enough for their chat files to be safe to read and overwrite.
/// </summary>
/// <remarks>
/// Polling is used rather than a WMI process-exit subscription: checking a handful
/// of process names every few seconds is cheap, needs no extra dependency or
/// elevated permissions, and keeps the timing rules unit-testable.
/// </remarks>
public sealed class ProviderWatcher(
    IProcessGuard processGuard,
    IReadOnlyList<string> providerProcessNames,
    WatchOptions? options = null,
    TimeProvider? timeProvider = null)
{
    private readonly IProcessGuard _processGuard = processGuard ?? throw new ArgumentNullException(nameof(processGuard));

    private readonly IReadOnlyList<string> _providerProcessNames =
        providerProcessNames ?? throw new ArgumentNullException(nameof(providerProcessNames));

    private readonly WatchOptions _options = Validated(options);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ProviderCloseDetector _detector = new(options);

    /// <summary>Raised when the provider is closed and settled.</summary>
    public event EventHandler<WatchTriggerReason>? SyncRequested;

    /// <summary>Raised when the provider starts or stops, for status reporting.</summary>
    public event EventHandler<bool>? ProviderRunningChanged;

    /// <summary>Whether the provider was running at the last check.</summary>
    public bool IsProviderRunning { get; private set; }

    /// <summary>
    /// Polls until <paramref name="cancellationToken"/> is cancelled, raising
    /// <see cref="SyncRequested"/> whenever a settled close is detected.
    /// </summary>
    public async Task WatchAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_options.PollInterval, _timeProvider);

        Poll();

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                Poll();
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping the watch is a normal shutdown, not a failure.
        }
    }

    /// <summary>Performs a single check. Exposed so a host can poll on its own timer.</summary>
    public void Poll()
    {
        var isRunning = _processGuard.GetRunningProcesses(_providerProcessNames).Count > 0;

        if (isRunning != IsProviderRunning)
        {
            IsProviderRunning = isRunning;
            ProviderRunningChanged?.Invoke(this, isRunning);
        }

        if (_detector.Observe(isRunning, _timeProvider.GetUtcNow()))
        {
            SyncRequested?.Invoke(this, WatchTriggerReason.ProviderClosed);
        }
    }

    private static WatchOptions Validated(WatchOptions? options)
    {
        var resolved = options ?? new WatchOptions();
        resolved.Validate();
        return resolved;
    }
}
