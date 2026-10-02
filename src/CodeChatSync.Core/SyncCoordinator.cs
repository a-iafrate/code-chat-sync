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

/// <summary>One finished run, kept so the window can show what happened and why.</summary>
public sealed record SyncLogEntry(DateTimeOffset At, SyncOutcomeStatus Status, string Summary, string? Detail)
{
    /// <summary>Whether this run is one the user should look at.</summary>
    public bool NeedsAttention =>
        Status is SyncOutcomeStatus.Aborted or SyncOutcomeStatus.Failed or SyncOutcomeStatus.NotConfigured
        || (Detail?.Contains("Conflict", StringComparison.Ordinal) ?? false);
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

    /// <summary>Raised when a run starts, so the window can say a sync is under way.</summary>
    public event EventHandler? Started;

    /// <summary>Whether a sync is running right now.</summary>
    public bool IsRunning => _gate.CurrentCount == 0;

    /// <summary>Outcome of the most recent run, for status display.</summary>
    public SyncOutcome? LastOutcome { get; private set; }

    /// <summary>How many finished runs are remembered for the window's log.</summary>
    private const int LogLength = 20;

    private readonly List<SyncLogEntry> _log = [];

    /// <summary>
    /// The most recent finished runs, newest first. Kept in memory only: it is a window
    /// into this session, not a record to carry between PCs.
    /// </summary>
    public IReadOnlyList<SyncLogEntry> RecentRuns
    {
        get
        {
            lock (_log)
            {
                return [.. _log];
            }
        }
    }

    /// <summary>
    /// Performs a configuration change only when no sync is in progress. The same
    /// gate also prevents a watcher-triggered run from starting mid-change.
    /// </summary>
    public async Task<bool> TryUpdateConfigurationAsync(Action update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            SyncTrace.Log("TryUpdateConfigurationAsync: gate busy, rejected immediately");
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
            // Logged here specifically because a rejection like this is otherwise invisible
            // in a trace: nothing downstream runs, so there is no other line to show for it.
            SyncTrace.Log($"RunAsync: gate busy, rejected immediately (dryRun={options?.DryRun ?? false})");
            return new SyncOutcome(SyncOutcomeStatus.AlreadyRunning, "A sync is already running.");
        }

        var isDryRun = options?.DryRun ?? false;
        try
        {
            if (!isDryRun)
            {
                Started?.Invoke(this, EventArgs.Empty);
            }

            var outcome = await Task.Run(() => Execute(options), cancellationToken).ConfigureAwait(false);

            // A dry run is an inspection, not a sync: it changes nothing, so it must not
            // replace the last real result, join the log, or above all be announced.
            // Listeners refresh themselves by inspecting — announcing a dry run would have
            // them answer their own notification, forever.
            if (!isDryRun)
            {
                LastOutcome = outcome;
                Record(outcome);
                Completed?.Invoke(this, outcome);
            }

            return outcome;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Default time an explicit request waits for a busy gate before giving up.</summary>
    private static readonly TimeSpan DefaultExplicitRetryWindow = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Runs a sync, waiting briefly and retrying once if the gate was busy — for an
    /// explicit, user-initiated request that has no "next time" to fall back on.
    /// </summary>
    /// <remarks>
    /// <see cref="RunAsync"/> correctly drops a request outright when the gate is busy: an
    /// automatic, watcher-triggered sync can afford that, since the next tool close picks
    /// up anything new regardless. A click on "Sync now" or a project's own sync button is
    /// singular — there is no "next time" — so silently dropping it when it only lost a
    /// brief race (most commonly against the window's own dry-run conflict check, which
    /// holds the same gate just as briefly, on startup and after every sync) reads as the
    /// sync having failed for no visible reason. This waits once for the gate to free up
    /// and retries exactly once; a sync still genuinely in progress after that is reported
    /// as busy exactly as <see cref="RunAsync"/> would report it.
    /// </remarks>
    public async Task<SyncOutcome> RunOrWaitAsync(
        SyncRunOptions? options = null,
        TimeSpan? retryWindow = null,
        CancellationToken cancellationToken = default)
    {
        var outcome = await RunAsync(options, cancellationToken).ConfigureAwait(false);
        if (outcome.Status != SyncOutcomeStatus.AlreadyRunning)
        {
            return outcome;
        }

        var window = retryWindow ?? DefaultExplicitRetryWindow;
        SyncTrace.Log($"RunOrWaitAsync: gate busy, waiting up to {window} for it to free up");

        // Waiting on the gate itself, rather than polling IsRunning, needs no arbitrary
        // poll interval and reacts the instant the busy run releases it. It is released
        // again immediately: this only asks whether the gate freed up in time, and the
        // RunAsync call below is what actually reserves it for this request.
        if (!await _gate.WaitAsync(window, cancellationToken).ConfigureAwait(false))
        {
            SyncTrace.Log("RunOrWaitAsync: gate still busy after the wait, giving up");
            return outcome;
        }

        SyncTrace.Log("RunOrWaitAsync: gate freed up, retrying");
        _gate.Release();
        return await RunAsync(options, cancellationToken).ConfigureAwait(false);
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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new SyncOutcome(SyncOutcomeStatus.Failed, $"The sync failed: {exception.Message}");
        }
    }

    private void Record(SyncOutcome outcome)
    {
        var entry = new SyncLogEntry(DateTimeOffset.Now, outcome.Status, outcome.Summary, Describe(outcome));
        lock (_log)
        {
            _log.Insert(0, entry);
            if (_log.Count > LogLength)
            {
                _log.RemoveRange(LogLength, _log.Count - LogLength);
            }
        }
    }

    /// <summary>
    /// Everything the summary leaves out: why a run stopped, and which files were not
    /// copied and for what reason. A sync that reports a conflict is useless without it.
    /// </summary>
    private static string? Describe(SyncOutcome outcome)
    {
        var lines = new List<string>();

        if (outcome.Result is { } result)
        {
            if (result.PrepareMessage is { Length: > 0 } prepare)
            {
                lines.Add(prepare);
            }

            foreach (var project in result.Projects)
            {
                foreach (var entry in project.Report.Entries.Where(item =>
                    item.Action is SyncAction.Conflict or SyncAction.Skipped))
                {
                    lines.Add($"{entry.Action}: {project.ProviderId}/{entry.RelativePath}"
                        + (entry.Reason is { Length: > 0 } reason ? $" — {reason}" : string.Empty));
                }

                if (project.Report.Registration?.Reason is { Length: > 0 } registration)
                {
                    lines.Add($"{project.ProviderId}: {registration}");
                }
            }

            foreach (var unresolved in result.Unresolved)
            {
                lines.Add($"Skipped {unresolved.Remote}: {unresolved.Reason}");
            }

            if (result.PublishMessage is { Length: > 0 } publish)
            {
                lines.Add(publish);
            }
        }
        else if (outcome.Status is not SyncOutcomeStatus.Completed)
        {
            // A run that never produced a result carries its reason in the summary alone.
            lines.Add(outcome.Summary);
        }

        return lines.Count == 0 ? null : string.Join(Environment.NewLine, lines);
    }

    private static string Summarize(SyncRunResult result)
    {
        if (result.Projects.Count == 0)
        {
            return "No projects are registered on this PC.";
        }

        var summary = $"{result.PushedCount} pushed, {result.PulledCount} pulled";

        if (result.ListedSessionCount > 0)
        {
            summary += $", {result.ListedSessionCount} restored chat(s) now listed";
        }

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
