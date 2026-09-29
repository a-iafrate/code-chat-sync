namespace CodeChatSync.Core;

/// <summary>How a requested sync ended.</summary>
public enum SyncOutcomeStatus
{
    /// <summary>The run completed, possibly with conflicts.</summary>
    Completed,

    /// <summary>A run was already in progress, so this request was dropped.</summary>
    AlreadyRunning,

    /// <summary>The run stopped before touching any file.</summary>
    Aborted,

    /// <summary>This PC is not configured for syncing yet.</summary>
    NotConfigured,

    /// <summary>The run failed unexpectedly.</summary>
    Failed
}

/// <summary>Result of a coordinated sync, in a form a UI can display directly.</summary>
public sealed record SyncOutcome(SyncOutcomeStatus Status, string Summary, SyncRunResult? Result = null)
{
    public bool HasConflicts => Result?.HasConflicts ?? false;

    /// <summary>Whether the run needs the user's attention.</summary>
    public bool NeedsAttention =>
        Status is SyncOutcomeStatus.Aborted or SyncOutcomeStatus.Failed or SyncOutcomeStatus.NotConfigured
        || HasConflicts;
}

/// <summary>
/// Runs syncs one at a time and turns the result into a short status message.
/// </summary>
/// <remarks>
/// The watch can ask for a sync while one is still running, for example when
/// several Visual Studio instances close together. Overlapping runs would copy the
/// same files twice and race on the baselines, so a request arriving during a run
/// is dropped rather than queued: the next close will pick up anything new.
/// </remarks>
public sealed class SyncCoordinator(Func<SyncOrchestrator> orchestratorFactory)
{
    private readonly Func<SyncOrchestrator> _orchestratorFactory =
        orchestratorFactory ?? throw new ArgumentNullException(nameof(orchestratorFactory));

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Raised when a run finishes, on the thread that ran it.</summary>
    public event EventHandler<SyncOutcome>? Completed;

    /// <summary>Whether a sync is running right now.</summary>
    public bool IsRunning => _gate.CurrentCount == 0;

    /// <summary>Outcome of the most recent run, for status display.</summary>
    public SyncOutcome? LastOutcome { get; private set; }

    /// <summary>
    /// Performs a configuration change only when no sync is in progress. The same
    /// gate also prevents a watcher-triggered run from starting mid-change.
    /// </summary>
    public async Task<bool> TryUpdateConfigurationAsync(Action update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        try
        {
            await Task.Run(update, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Runs a sync unless one is already in progress.</summary>
    public async Task<SyncOutcome> RunAsync(SyncRunOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return new SyncOutcome(SyncOutcomeStatus.AlreadyRunning, "A sync is already running.");
        }

        try
        {
            var outcome = await Task.Run(() => Execute(options), cancellationToken).ConfigureAwait(false);
            LastOutcome = outcome;
            Completed?.Invoke(this, outcome);
            return outcome;
        }
        finally
        {
            _gate.Release();
        }
    }

    private SyncOutcome Execute(SyncRunOptions? options)
    {
        try
        {
            var result = _orchestratorFactory().Run(options);

            if (result.Aborted)
            {
                return new SyncOutcome(SyncOutcomeStatus.Aborted, result.AbortReason ?? "The sync was stopped.", result);
            }

            return new SyncOutcome(SyncOutcomeStatus.Completed, Summarize(result), result);
        }
        catch (SyncConfigurationException exception)
        {
            return new SyncOutcome(SyncOutcomeStatus.NotConfigured, exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new SyncOutcome(SyncOutcomeStatus.Failed, $"The sync failed: {exception.Message}");
        }
    }

    private static string Summarize(SyncRunResult result)
    {
        if (result.Projects.Count == 0)
        {
            return "No projects are registered on this PC.";
        }

        var summary = $"{result.PushedCount} pushed, {result.PulledCount} pulled";

        if (result.HasConflicts)
        {
            summary += $", {result.ConflictCount} conflicts";
        }

        if (result.HasBlockedPulls)
        {
            summary += ", some files still waiting for the tool to close";
        }

        return summary + ".";
    }
}
