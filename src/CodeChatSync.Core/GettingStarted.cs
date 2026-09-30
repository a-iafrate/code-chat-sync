namespace CodeChatSync.Core;

/// <summary>Setup steps a new user completes before chats sync on this PC.</summary>
/// <param name="SyncFolderChosen">A private sync folder is configured.</param>
/// <param name="RemoteConnected">The sync folder has a Git <c>origin</c>; optional, chats stay local without it.</param>
/// <param name="ProjectAdded">At least one project is registered on this PC.</param>
/// <param name="FirstSyncCompleted">A registered project has been synced at least once on this PC.</param>
public sealed record GettingStartedProgress(
    bool SyncFolderChosen,
    bool RemoteConnected,
    bool ProjectAdded,
    bool FirstSyncCompleted)
{
    /// <summary>Number of steps shown in the checklist, including the optional remote.</summary>
    public const int StepCount = 4;

    /// <summary>The required steps are done; connecting a remote stays optional.</summary>
    public bool IsComplete => SyncFolderChosen && ProjectAdded && FirstSyncCompleted;

    public int CompletedSteps =>
        (SyncFolderChosen ? 1 : 0) + (RemoteConnected ? 1 : 0) + (ProjectAdded ? 1 : 0) + (FirstSyncCompleted ? 1 : 0);
}

/// <summary>Decides when first-run guidance is shown; the UI only renders it.</summary>
public static class GettingStarted
{
    /// <summary>
    /// Opens the setup wizard by itself only on a fresh install, so existing
    /// installations that predate the wizard are not interrupted.
    /// </summary>
    public static bool ShouldOpenWizardAutomatically(LocalConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return !config.OnboardingWizardSeen
            && string.IsNullOrWhiteSpace(config.SyncRootPath)
            && config.Projects.Count == 0;
    }

    /// <summary>The checklist stays visible until setup is complete or the user hides it.</summary>
    public static bool ShouldShowChecklist(LocalConfig config, GettingStartedProgress progress)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(progress);

        return !config.GettingStartedDismissed && !progress.IsComplete;
    }

    public static GettingStartedProgress Evaluate(LocalConfig config, bool remoteConnected, bool firstSyncCompleted)
    {
        ArgumentNullException.ThrowIfNull(config);

        var folderChosen = !string.IsNullOrWhiteSpace(config.SyncRootPath);
        var projectAdded = config.Projects.Count > 0;
        return new GettingStartedProgress(
            folderChosen,
            folderChosen && remoteConnected,
            projectAdded,
            projectAdded && firstSyncCompleted);
    }
}
