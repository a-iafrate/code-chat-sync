namespace CodeChatSync.Core;

/// <summary>What happened to a single chat file during a sync.</summary>
public enum SyncAction
{
    /// <summary>Both sides already matched.</summary>
    Unchanged,

    /// <summary>Copied from this PC into the sync folder.</summary>
    Pushed,

    /// <summary>Copied from the sync folder onto this PC.</summary>
    Pulled,

    /// <summary>Changed on both sides: left untouched and reported.</summary>
    Conflict,

    /// <summary>Skipped because the chat was in use or the provider was running.</summary>
    Skipped
}

/// <summary>Outcome for one chat file.</summary>
public sealed record SyncEntryResult
{
    public required string RelativePath { get; init; }

    public required SyncAction Action { get; init; }

    /// <summary>Path of the backup taken before a local file was overwritten.</summary>
    public string? BackupPath { get; init; }

    public string? Reason { get; init; }

    /// <summary>
    /// True when the file was left alone only because the provider's tool is running.
    /// Closing the tool and syncing again will complete it.
    /// </summary>
    public bool IsBlockedByProvider { get; init; }
}

/// <summary>Outcome of a whole sync run.</summary>
public sealed record SyncReport
{
    public required IReadOnlyList<SyncEntryResult> Entries { get; init; }

    public int PushedCount => Entries.Count(entry => entry.Action is SyncAction.Pushed);

    public int PulledCount => Entries.Count(entry => entry.Action is SyncAction.Pulled);

    public int ConflictCount => Entries.Count(entry => entry.Action is SyncAction.Conflict);

    public int SkippedCount => Entries.Count(entry => entry.Action is SyncAction.Skipped);

    public int UnchangedCount => Entries.Count(entry => entry.Action is SyncAction.Unchanged);

    public bool HasConflicts => ConflictCount > 0;

    /// <summary>Files that still need the provider's tool to be closed.</summary>
    public int BlockedByProviderCount => Entries.Count(entry => entry.IsBlockedByProvider);

    public bool HasBlockedPulls => BlockedByProviderCount > 0;
}
