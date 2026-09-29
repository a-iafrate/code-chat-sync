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
    /// <see langword="null"/> with the reason when Git cannot be used there.
    /// </summary>
    public static GitSyncPublisher? TryCreate(string syncRootPath, out string? unavailableReason)
    {
        var repository = new SyncRepository(syncRootPath);

        var availability = repository.CheckGitAvailability();
        if (!availability.IsAvailable)
        {
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
            unavailableReason = $"Git is unusable here ({exception.Message}); syncing files only.";
            return null;
        }

        unavailableReason = null;
        return new GitSyncPublisher(repository);
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

        return pull.Status switch
        {
            PullStatus.UpToDate => SyncPublishResult.Ok("Pulled the latest chats from the sync remote."),
            PullStatus.NoRemote => SyncPublishResult.Ok("The sync repository has no remote yet; working locally."),
            PullStatus.NoUpstream => SyncPublishResult.Ok("First push from this PC: nothing to pull yet."),
            _ => SyncPublishResult.Stop(
                $"Could not pull the sync repository, so nothing was changed locally:{Environment.NewLine}{pull.Message}")
        };
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
