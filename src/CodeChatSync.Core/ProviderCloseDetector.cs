namespace CodeChatSync.Core;

/// <summary>Timing of the automatic watch.</summary>
public sealed record WatchOptions
{
    /// <summary>How often the provider's processes are checked.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long to wait after the last provider process exits before syncing.
    /// </summary>
    /// <remarks>
    /// Visual Studio keeps flushing its chat files for a moment after the window
    /// closes, and closing one instance while another is starting is common. Waiting
    /// avoids copying a half-written transcript.
    /// </remarks>
    public TimeSpan SettleDelay { get; init; } = TimeSpan.FromSeconds(20);

    public void Validate()
    {
        if (PollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(PollInterval), PollInterval, "The poll interval must be positive.");
        }

        if (SettleDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(SettleDelay), SettleDelay, "The settle delay cannot be negative.");
        }
    }
}

/// <summary>
/// Decides when the provider has been closed long enough for a sync to be safe.
/// </summary>
/// <remarks>
/// Kept free of timers and processes so the timing rules can be tested exactly,
/// without waiting in real time.
/// </remarks>
public sealed class ProviderCloseDetector(WatchOptions? options = null)
{
    private readonly WatchOptions _options = Validated(options);

    private bool _wasRunning;
    private DateTimeOffset? _closedAt;

    /// <summary>Whether the provider was running at the last observation.</summary>
    public bool IsProviderRunning => _wasRunning;

    /// <summary>
    /// Whether a close was seen and the settle delay has not elapsed yet, so a sync
    /// is still pending.
    /// </summary>
    public bool IsWaitingToSettle => _closedAt is not null;

    /// <summary>
    /// Records one observation and reports whether a sync should run now.
    /// </summary>
    /// <remarks>
    /// A sync is only triggered after the provider is seen closing. Starting the
    /// watch while the provider is already closed does not sync on its own: that
    /// would re-copy everything on every app start.
    /// </remarks>
    public bool Observe(bool isProviderRunning, DateTimeOffset now)
    {
        if (isProviderRunning)
        {
            _wasRunning = true;
            _closedAt = null;
            return false;
        }

        if (_wasRunning)
        {
            _wasRunning = false;
            _closedAt = now;
            return _options.SettleDelay <= TimeSpan.Zero;
        }

        if (_closedAt is not { } closedAt || now - closedAt < _options.SettleDelay)
        {
            return false;
        }

        _closedAt = null;
        return true;
    }

    /// <summary>
    /// Forgets a pending close, so a sync that already ran is not repeated.
    /// </summary>
    public void Reset() => _closedAt = null;

    private static WatchOptions Validated(WatchOptions? options)
    {
        var resolved = options ?? new WatchOptions();
        resolved.Validate();
        return resolved;
    }
}
