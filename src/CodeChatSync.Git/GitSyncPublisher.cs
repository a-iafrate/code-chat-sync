using CodeChatSync.Core;

namespace CodeChatSync.Git;

/// <summary>
/// Publishes the sync folder through Git: pull before a run, commit and push after.
/// </summary>
/// <remarks>
/// When Git is unusable the sync still copies files into the sync folder, so chats
/// are never lost just because the sync repository cannot be reached.
/// </remarks>
public sealed class GitSyncPublisher(SyncRepository repository) : ISyncPublisher
{
    private readonly SyncRepository _repository = repository ?? throw new ArgumentNullException(nameof(repository));

    /// <summary>Description of the machine that produced a commit.</summary>
    public string MachineName { get; init; } = Environment.MachineName;

    /// <summary>
    /// Builds a publisher for <paramref name="syncRootPath"/>, or returns
    /// <see langword="null"/> with the reason when the folder is not a Git repository.
    /// When the folder is a Git repository but Git cannot be used, the returned publisher
    /// stops the run: syncing without pulling could restore stale chats.
    /// </summary>
    public static ISyncPublisher? TryCreate(string syncRootPath, out string? unavailableReason)
    {
        var repository = new SyncRepository(syncRootPath);
        var isRepository = Directory.Exists(Path.Combine(syncRootPath, ".git"))
            || File.Exists(Path.Combine(syncRootPath, ".git"));

        var availability = repository.CheckGitAvailability();
        if (!availability.IsAvailable)
        {
            if (isRepository)
            {
                unavailableReason =
                    $"Git is not available ({availability.Description}), so the sync repository cannot be pulled. "
                    + "Install Git or fix its path, then sync again.";
                return new BlockedPublisher(unavailableReason);
            }

            unavailableReason = $"Git is not available ({availability.Description}); syncing files only.";
            return null;
        }

        try
        {
            if (!repository.GetStatus().IsGitRepository)
            {
                unavailableReason =
                    "The sync folder is not a Git repository; syncing files only. "
                    + "Run 'codechatsync config set-sync-root <path> --init' to turn it into one.";
                return null;
            }
        }
        catch (GitCommandException exception)
        {
            unavailableReason = $"Git is unusable here ({exception.Message}); the sync repository cannot be pulled.";
            return isRepository ? new BlockedPublisher(unavailableReason) : null;
        }

        unavailableReason = null;
        return new GitSyncPublisher(repository);
    }

    /// <summary>Stops every run before files are copied, reporting why Git cannot be used.</summary>
    private sealed class BlockedPublisher(string reason) : ISyncPublisher
    {
        public SyncPublishResult PrepareForSync() => SyncPublishResult.Stop(reason);

        public SyncPublishResult PublishChanges(int pushedCount) => SyncPublishResult.Stop(reason);
    }

    public SyncPublishResult PrepareForSync()
    {
        PullOutcome pull;
        try
        {
            pull = _repository.Pull();
        }
        catch (GitCommandException exception)
        {
            return SyncPublishResult.Stop($"The sync repository could not be pulled: {exception.Message}");
        }

        if (pull.Status is PullStatus.Failed)
        {
            return SyncPublishResult.Stop(
                $"Could not pull the sync repository, so nothing was changed locally:{Environment.NewLine}{pull.Message}");
        }

        var message = pull.Status switch
        {
            PullStatus.NoRemote => "The sync repository has no remote yet; working locally.",
            PullStatus.NoUpstream => "First push from this PC: nothing to pull yet.",
            _ => "Pulled the latest chats from the sync remote."
        };

        // After the pull, so the attributes file cannot block a fast-forward checkout on a
        // branch this PC has not pushed yet.
        if (EnsureVerbatimContent() is { Length: > 0 } warning)
        {
            message += Environment.NewLine + warning;
        }

        return SyncPublishResult.Ok(message);
    }

    /// <summary>
    /// Keeps Git from rewriting chat files, returning a warning when the settings could
    /// not be applied. A failure here does not stop the run: it only means line-ending
    /// conflicts stay possible until the next attempt succeeds.
    /// </summary>
    private string? EnsureVerbatimContent()
    {
        try
        {
            _repository.EnsureVerbatimContent();
            return null;
        }
        catch (Exception exception) when (exception is GitCommandException or IOException or UnauthorizedAccessException)
        {
            return "Could not tell Git to store chats unchanged, so files may still be reported as conflicting "
                + $"after a line-ending change: {exception.Message}";
        }
    }

    public SyncPublishResult PublishChanges(int pushedCount)
    {
        try
        {
            if (!_repository.Commit($"Sync chats from {MachineName} ({pushedCount} file(s))"))
            {
                return SyncPublishResult.Ok("Nothing new to commit in the sync repository.");
            }

            if (!_repository.GetStatus().HasRemote)
            {
                return SyncPublishResult.Ok("Committed the updated chats. No remote is configured, so they stay on this PC.");
            }

            var push = _repository.Push();
            return push.Succeeded
                ? SyncPublishResult.Ok("Committed and pushed the updated chats.")
                : SyncPublishResult.Stop(
                    $"Committed the updated chats, but the push failed:{Environment.NewLine}{push.ErrorMessage}");
        }
        catch (GitCommandException exception)
        {
            return SyncPublishResult.Stop($"Git failed after the files were copied: {exception.Message}");
        }
    }
}
