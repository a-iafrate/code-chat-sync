using CodeChatSync.Core;
using CodeChatSync.Git;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CodeChatSync.App;

/// <summary>
/// First-run guidance: the setup wizard, the "Get started" checklist on the Sync
/// page, and contextual hints (a guided tour and per-step tips).
/// </summary>
public sealed partial class MainWindow
{
    private const int SetupWelcomeStep = 0;
    private const int SetupRepositoryStep = 1;
    private const int SetupProjectStep = 2;
    private const int SetupLastStep = 4;

    private static readonly string[] SetupTitles =
    [
        "Welcome to CodeChatSync",
        "Choose your sync repository",
        "Add your projects",
        "How syncing works",
        "You're ready"
    ];

    private LocalConfig? _guidanceConfig;
    private GettingStartedProgress? _progress;
    private bool _wizardCheckedOnLoad;
    private bool _setupDialogOpen;
    private int _setupStep;
    private GuideHint[]? _tourSteps;
    private GuideHint? _pendingHint;
    private int _tourIndex;

    private sealed record GuideHint(string Page, Func<FrameworkElement> Target, string Title, string Subtitle);

    private void OnWindowRootLoaded(object sender, RoutedEventArgs e)
    {
        if (_wizardCheckedOnLoad)
        {
            return;
        }

        _wizardCheckedOnLoad = true;
        try
        {
            if (GettingStarted.ShouldOpenWizardAutomatically(LocalConfig.Load()))
            {
                _ = ShowSetupWizardAsync();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // LoadSettingsAsync reports configuration problems in Settings; the wizard just stays closed.
        }
    }

    // ---- Get started checklist ----

    private void UpdateGettingStarted(LocalConfig config, bool remoteConnected, bool anyProjectSynced)
    {
        _guidanceConfig = config;
        _progress = GettingStarted.Evaluate(config, remoteConnected, anyProjectSynced);
        RenderGettingStarted();
    }

    private void MarkFirstSyncCompleted(SyncOutcome outcome)
    {
        if (outcome.Status == SyncOutcomeStatus.Completed
            && _progress is { ProjectAdded: true, FirstSyncCompleted: false })
        {
            _progress = _progress with { FirstSyncCompleted = true };
            RenderGettingStarted();
        }
    }

    private void RenderGettingStarted()
    {
        if (_guidanceConfig is null || _progress is not { } progress
            || !GettingStarted.ShouldShowChecklist(_guidanceConfig, progress))
        {
            GettingStartedCard.Visibility = Visibility.Collapsed;
            return;
        }

        GettingStartedCard.Visibility = Visibility.Visible;
        GettingStartedProgressBar.Value = progress.CompletedSteps;

        // The first pending required step gets the accent button so the next action is obvious.
        var steps = new (bool Done, bool Required, FontIcon Icon, Button Button, string Name)[]
        {
            (progress.SyncFolderChosen, true, FolderStepIcon, FolderStepButton, "choose a private sync folder"),
            (progress.RemoteConnected, false, RemoteStepIcon, RemoteStepButton, "connect a Git remote"),
            (progress.ProjectAdded, true, ProjectStepIcon, ProjectStepButton, "add a project"),
            (progress.FirstSyncCompleted, true, SyncStepIcon, SyncStepButton, "run your first sync")
        };
        var next = steps.FirstOrDefault(step => !step.Done && step.Required);
        foreach (var step in steps)
        {
            step.Icon.Glyph = step.Done ? "\uEC61" : "\uEA3A";
            step.Icon.Style = (Style)WindowRoot.Resources[step.Done ? "StepDoneIconStyle" : "StepPendingIconStyle"];
            step.Button.Visibility = step.Done ? Visibility.Collapsed : Visibility.Visible;
            if (step.Button == next.Button)
            {
                step.Button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            }
            else
            {
                step.Button.ClearValue(FrameworkElement.StyleProperty);
            }
        }

        GettingStartedSummaryText.Text = next.Button is null
            ? $"{progress.CompletedSteps} of {GettingStartedProgress.StepCount} steps done."
            : $"{progress.CompletedSteps} of {GettingStartedProgress.StepCount} steps done. Next: {next.Name}.";
    }

    private async void OnHideGettingStartedClick(object sender, RoutedEventArgs e)
    {
        if (_guidanceConfig is not null)
        {
            _guidanceConfig.GettingStartedDismissed = true;
        }

        GettingStartedCard.Visibility = Visibility.Collapsed;
        await TrySaveOnboardingStateAsync(checklistDismissed: true);
    }

    private void OnFolderStepClick(object sender, RoutedEventArgs e) => ShowHint(new GuideHint(
        "settings", () => SyncFolderTextBox, "Choose a sync folder",
        "Browse to a new empty folder and turn on Initialize, or pick an existing clone of your private chat repository. Then choose Save repository settings."));

    private void OnRemoteStepClick(object sender, RoutedEventArgs e) => ShowHint(new GuideHint(
        "settings", () => RemoteTextBox, "Connect your private repository",
        "Paste the URL of an empty private GitHub or Azure DevOps repository, then choose Save repository settings. Git's credential helper handles sign-in."));

    private void OnProjectStepClick(object sender, RoutedEventArgs e)
    {
        AddProjectExpander.IsExpanded = true;
        ShowHint(new GuideHint(
            "projects", () => AddProjectExpander, "Add a project",
            "Choose the provider. For Visual Studio, browse to a folder in the project's Git repository; for Claude Code, choose Find Claude Code projects and pick one."));
    }

    private void OnSyncStepClick(object sender, RoutedEventArgs e) => ShowHint(new GuideHint(
        "sync", () => SyncNowButton, "Run your first sync",
        "Close Visual Studio and Claude Code, then choose Sync now. CodeChatSync never touches chat files while they are open."));

    // ---- Contextual hints and guided tour ----

    private void OnStartTourClick(object sender, RoutedEventArgs e) => StartTour();

    private void StartTour()
    {
        _tourSteps =
        [
            new("sync", () => SyncNowButton, "Sync now",
                "Archives chats from your registered projects and restores them here. If a chat tool is open, CodeChatSync waits for it to close."),
            new("sync", () => AutomaticSyncToggle, "Automatic sync",
                "When this is on, chats sync by themselves shortly after Visual Studio or Claude Code closes."),
            new("projects", () => AddProjectExpander, "Projects",
                "Add Visual Studio (Copilot) or Claude Code projects here. Each project is matched across PCs by its Git remote, and one project can use both."),
            new("projects", () => RestoreSectionTitle, "Chats to restore",
                "Choose which archived chats come back on this PC. Everything stays in your private repository either way."),
            new("settings", () => SyncFolderTextBox, "Sync repository",
                "Your private chat repository: a local folder and, optionally, its Git remote."),
            new("settings", () => OpenSetupGuideButton, "Need help later?",
                "Reopen the setup guide and the Get started checklist, or take this tour again, from here.")
        ];
        _tourIndex = 0;
        ShowHint(_tourSteps[0]);
    }

    private void ShowHint(GuideHint hint)
    {
        // Reopening a tip that is still closing is ignored, so wait for Closed first.
        if (GuideTip.IsOpen)
        {
            _pendingHint = hint;
            GuideTip.IsOpen = false;
            return;
        }

        OpenHint(hint);
    }

    private void OnGuideTipClosed(TeachingTip sender, TeachingTipClosedEventArgs args)
    {
        if (_pendingHint is { } hint)
        {
            _pendingHint = null;
            OpenHint(hint);
        }
    }

    private void OpenHint(GuideHint hint)
    {
        NavigateTo(hint.Page);

        var inTour = _tourSteps is not null;
        GuideTip.Title = hint.Title;
        GuideTip.Subtitle = inTour ? $"{hint.Subtitle}\n\nStep {_tourIndex + 1} of {_tourSteps!.Length}" : hint.Subtitle;
        GuideTip.ActionButtonContent = inTour
            ? (_tourIndex == _tourSteps!.Length - 1 ? "Done" : "Next")
            : null;
        GuideTip.CloseButtonContent = inTour ? "End tour" : "Got it";

        // The page was collapsed until now: wait for layout before anchoring the tip.
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            var target = hint.Target();
            target.StartBringIntoView();
            GuideTip.Target = target;
            GuideTip.IsOpen = true;
        });
    }

    private void OnGuideTipActionClick(TeachingTip sender, object args)
    {
        if (_tourSteps is null || ++_tourIndex >= _tourSteps.Length)
        {
            EndTour();
            return;
        }

        ShowHint(_tourSteps[_tourIndex]);
    }

    private void OnGuideTipCloseClick(TeachingTip sender, object args) => EndTour();

    private void EndTour()
    {
        _tourSteps = null;
        _pendingHint = null;
        GuideTip.IsOpen = false;
    }

    private void NavigateTo(string page) => NavView.SelectedItem = page switch
    {
        "sync" => SyncNavItem,
        "projects" => ProjectsNavItem,
        "settings" => SettingsNavItem,
        _ => throw new ArgumentOutOfRangeException(nameof(page), page, "Unknown page.")
    };

    // ---- Setup wizard ----

    private async void OnOpenSetupGuideClick(object sender, RoutedEventArgs e)
    {
        if (_guidanceConfig is not null)
        {
            _guidanceConfig.GettingStartedDismissed = false;
        }

        RenderGettingStarted();
        await TrySaveOnboardingStateAsync(checklistDismissed: false);
        await ShowSetupWizardAsync();
    }

    private async Task ShowSetupWizardAsync()
    {
        if (_setupDialogOpen)
        {
            return;
        }

        _setupDialogOpen = true;
        EndTour();
        try
        {
            SetupFolderTextBox.Text = _configuredFolder ?? string.Empty;
            SetupRemoteTextBox.Text = _loadedOrigin ?? string.Empty;
            SetupProjectTextBox.Text = string.Empty;
            SetupDialog.RequestedTheme = WindowRoot.ActualTheme;
            ShowSetupStep(SetupWelcomeStep);

            var result = await SetupDialog.ShowAsync();
            await TrySaveOnboardingStateAsync(wizardSeen: true);
            if (result != ContentDialogResult.Primary)
            {
                return;
            }

            if (SetupTourCheckBox.IsChecked == true)
            {
                StartTour();
            }

            if (SetupRunSyncCheckBox.IsEnabled && SetupRunSyncCheckBox.IsChecked == true)
            {
                await RunSyncNowAsync();
            }
        }
        finally
        {
            _setupDialogOpen = false;
        }
    }

    private void ShowSetupStep(int step)
    {
        _setupStep = step;
        SetupWelcomePage.Visibility = step == SetupWelcomeStep ? Visibility.Visible : Visibility.Collapsed;
        SetupRepositoryPage.Visibility = step == SetupRepositoryStep ? Visibility.Visible : Visibility.Collapsed;
        SetupProjectPage.Visibility = step == SetupProjectStep ? Visibility.Visible : Visibility.Collapsed;
        SetupHowPage.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;
        SetupDonePage.Visibility = step == SetupLastStep ? Visibility.Visible : Visibility.Collapsed;
        SetupInfoBar.IsOpen = false;

        SetupDialog.Title = SetupTitles[step];
        SetupStepText.Text = $"Step {step + 1} of {SetupTitles.Length}";
        SetupDialog.PrimaryButtonText = step switch
        {
            SetupWelcomeStep => "Get started",
            SetupLastStep => "Finish",
            _ => "Next"
        };
        // An empty text hides the button.
        SetupDialog.SecondaryButtonText = step == SetupWelcomeStep ? string.Empty : "Back";
        SetupDialog.CloseButtonText = step == SetupLastStep ? string.Empty : "Skip setup";

        if (step == SetupProjectStep)
        {
            UpdateSetupProjectsText();
        }
        else if (step == SetupLastStep)
        {
            var canSync = _configuredFolder is { Length: > 0 } && _guidanceConfig is { Projects.Count: > 0 };
            SetupRunSyncCheckBox.IsEnabled = canSync;
            SetupRunSyncCheckBox.IsChecked = canSync;
            SetupDoneText.Text = canSync
                ? "Setup is done. Close Visual Studio and Claude Code before the first sync: CodeChatSync waits while they are open."
                : "You can finish the remaining steps later from the Get started checklist on the Sync page.";
        }
    }

    private async void OnSetupPrimaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_setupStep == SetupLastStep)
        {
            return;
        }

        args.Cancel = true;
        if (_setupStep != SetupRepositoryStep)
        {
            ShowSetupStep(_setupStep + 1);
            return;
        }

        var deferral = args.GetDeferral();
        sender.IsPrimaryButtonEnabled = false;
        try
        {
            if (await SaveSetupRepositoryAsync())
            {
                ShowSetupStep(_setupStep + 1);
            }
        }
        finally
        {
            sender.IsPrimaryButtonEnabled = true;
            deferral.Complete();
        }
    }

    private void OnSetupSecondaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        ShowSetupStep(Math.Max(SetupWelcomeStep, _setupStep - 1));
    }

    private async Task<bool> SaveSetupRepositoryAsync()
    {
        var folder = SetupFolderTextBox.Text.Trim();
        var remote = SetupRemoteTextBox.Text.Trim();
        if (folder.Length == 0)
        {
            ShowSetupMessage("Choose a sync folder, or choose Skip setup to do it later.", InfoBarSeverity.Error);
            return false;
        }

        var unchanged = _configuredFolder is { Length: > 0 } configured
            && string.Equals(Path.GetFullPath(folder), Path.GetFullPath(configured), StringComparison.OrdinalIgnoreCase)
            && (remote.Length == 0 || string.Equals(remote, _loadedOrigin, StringComparison.Ordinal));
        if (unchanged)
        {
            return true;
        }

        try
        {
            if (!await _syncHost.SaveSettingsAsync(folder, remote, SetupInitializeCheckBox.IsChecked == true))
            {
                ShowSetupMessage("A sync is in progress. Try again when it finishes.", InfoBarSeverity.Warning);
                return false;
            }

            await LoadSettingsAsync();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or GitCommandException or NotSupportedException)
        {
            ShowSetupMessage($"Could not save the sync repository: {exception.Message}", InfoBarSeverity.Error);
            return false;
        }
    }

    private async void OnSetupAddProjectClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SetupProjectTextBox.Text))
        {
            ShowSetupMessage("Choose a project folder first.", InfoBarSeverity.Error);
            return;
        }

        SetupAddProjectButton.IsEnabled = false;
        try
        {
            if (!await _syncHost.AddProjectAsync(SetupProjectTextBox.Text, null, null))
            {
                ShowSetupMessage("A sync is in progress. Try again when it finishes.", InfoBarSeverity.Warning);
                return;
            }

            SetupProjectTextBox.Text = string.Empty;
            await LoadSettingsAsync();
            UpdateSetupProjectsText();
            ShowSetupMessage("Project added. Add another one or choose Next.", InfoBarSeverity.Success);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidDataException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            ShowSetupMessage($"Could not add the project: {exception.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            SetupAddProjectButton.IsEnabled = true;
        }
    }

    private void UpdateSetupProjectsText()
    {
        var projects = _guidanceConfig?.Projects ?? [];
        SetupProjectsText.Text = projects.Count == 0
            ? "No projects registered on this PC yet."
            : "Registered on this PC: " + string.Join(", ", projects.Select(project =>
                ProjectIdentity.TryFromRemote(project.Remote, out var identity) && identity is not null
                    ? identity.Slug
                    : project.Remote));
    }

    private async void OnSetupBrowseFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (await PickFolderAsync() is { } path)
            {
                SetupFolderTextBox.Text = path;
            }
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException
            or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowSetupMessage($"Could not choose a folder: {exception.Message}", InfoBarSeverity.Error);
        }
    }

    private async void OnSetupBrowseProjectClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (await PickFolderAsync() is { } path)
            {
                SetupProjectTextBox.Text = path;
            }
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException
            or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowSetupMessage($"Could not choose a project folder: {exception.Message}", InfoBarSeverity.Error);
        }
    }

    private void ShowSetupMessage(string message, InfoBarSeverity severity)
    {
        SetupInfoBar.Message = message;
        SetupInfoBar.Severity = severity;
        SetupInfoBar.IsOpen = true;
    }

    private async Task TrySaveOnboardingStateAsync(bool? wizardSeen = null, bool? checklistDismissed = null)
    {
        try
        {
            // A refusal during an active sync only means the choice is asked again next time.
            await _syncHost.SaveOnboardingStateAsync(wizardSeen, checklistDismissed);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidDataException or NotSupportedException)
        {
            ShowSettingsMessage($"Could not save the setup guide state: {exception.Message}", InfoBarSeverity.Warning);
        }
    }
}
