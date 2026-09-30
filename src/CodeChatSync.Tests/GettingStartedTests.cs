using System.Text.Json;
using CodeChatSync.Core;
using CodeChatSync.Tests.TestSupport;

namespace CodeChatSync.Tests;

public sealed class GettingStartedTests : IDisposable
{
    private readonly TempDirectory _root = new();

    public void Dispose() => _root.Dispose();

    [Fact]
    public void ShouldOpenWizardAutomatically_IsTrueOnlyForAFreshConfig()
    {
        Assert.True(GettingStarted.ShouldOpenWizardAutomatically(new LocalConfig()));

        var wizardSeen = new LocalConfig { OnboardingWizardSeen = true };
        var syncFolderChosen = new LocalConfig { SyncRootPath = _root.Combine("sync") };
        var projectAdded = new LocalConfig();
        AddProject(projectAdded);

        Assert.False(GettingStarted.ShouldOpenWizardAutomatically(wizardSeen));
        Assert.False(GettingStarted.ShouldOpenWizardAutomatically(syncFolderChosen));
        Assert.False(GettingStarted.ShouldOpenWizardAutomatically(projectAdded));
    }

    [Fact]
    public void Evaluate_OnlyCountsRemoteWhenASyncFolderIsChosen()
    {
        var withoutFolder = GettingStarted.Evaluate(new LocalConfig(), remoteConnected: true, firstSyncCompleted: false);
        var withFolder = GettingStarted.Evaluate(
            new LocalConfig { SyncRootPath = _root.Combine("sync") },
            remoteConnected: true,
            firstSyncCompleted: false);

        Assert.False(withoutFolder.SyncFolderChosen);
        Assert.False(withoutFolder.RemoteConnected);
        Assert.True(withFolder.SyncFolderChosen);
        Assert.True(withFolder.RemoteConnected);
    }

    [Fact]
    public void Evaluate_OnlyCountsFirstSyncWhenAProjectIsAdded()
    {
        var withoutProject = GettingStarted.Evaluate(
            new LocalConfig { SyncRootPath = _root.Combine("sync") },
            remoteConnected: false,
            firstSyncCompleted: true);
        var withProject = new LocalConfig { SyncRootPath = _root.Combine("sync") };
        AddProject(withProject);
        var evaluated = GettingStarted.Evaluate(withProject, remoteConnected: false, firstSyncCompleted: true);

        Assert.False(withoutProject.ProjectAdded);
        Assert.False(withoutProject.FirstSyncCompleted);
        Assert.True(evaluated.ProjectAdded);
        Assert.True(evaluated.FirstSyncCompleted);
    }

    [Fact]
    public void Evaluate_CountsCompletedStepsAndRequiresFolderProjectAndFirstSyncButNotRemote()
    {
        var config = new LocalConfig { SyncRootPath = _root.Combine("sync") };
        AddProject(config);

        var progress = GettingStarted.Evaluate(config, remoteConnected: false, firstSyncCompleted: true);

        Assert.Equal(3, progress.CompletedSteps);
        Assert.True(progress.IsComplete);
        Assert.Equal(GettingStartedProgress.StepCount, 4);

        var missingFolder = GettingStarted.Evaluate(
            new LocalConfig(),
            remoteConnected: true,
            firstSyncCompleted: true);
        var missingProject = GettingStarted.Evaluate(
            new LocalConfig { SyncRootPath = _root.Combine("sync") },
            remoteConnected: false,
            firstSyncCompleted: true);
        var missingFirstSync = GettingStarted.Evaluate(config, remoteConnected: false, firstSyncCompleted: false);

        Assert.False(missingFolder.IsComplete);
        Assert.False(missingProject.IsComplete);
        Assert.False(missingFirstSync.IsComplete);

        var withRemote = GettingStarted.Evaluate(config, remoteConnected: true, firstSyncCompleted: true);
        Assert.Equal(4, withRemote.CompletedSteps);
        Assert.True(withRemote.IsComplete);
    }

    [Fact]
    public void ShouldShowChecklist_IsHiddenWhenDismissedOrComplete()
    {
        var config = new LocalConfig();
        var incomplete = new GettingStartedProgress(true, false, true, false);
        var complete = new GettingStartedProgress(true, false, true, true);

        Assert.True(GettingStarted.ShouldShowChecklist(config, incomplete));
        Assert.False(GettingStarted.ShouldShowChecklist(new LocalConfig { GettingStartedDismissed = true }, incomplete));
        Assert.False(GettingStarted.ShouldShowChecklist(config, complete));
    }

    [Fact]
    public void Methods_ThrowForNullArguments()
    {
        Assert.Throws<ArgumentNullException>(() => GettingStarted.ShouldOpenWizardAutomatically(null!));
        Assert.Throws<ArgumentNullException>(() => GettingStarted.ShouldShowChecklist(null!, new GettingStartedProgress(false, false, false, false)));
        Assert.Throws<ArgumentNullException>(() => GettingStarted.ShouldShowChecklist(new LocalConfig(), null!));
        Assert.Throws<ArgumentNullException>(() => GettingStarted.Evaluate(null!, remoteConnected: false, firstSyncCompleted: false));
    }

    [Fact]
    public void LocalConfig_SaveAndLoad_RoundTripsGettingStartedFlagsAndUsesExpectedJsonNames()
    {
        var path = _root.Combine("nested", "local-config.json");
        var config = new LocalConfig
        {
            OnboardingWizardSeen = true,
            GettingStartedDismissed = true
        };

        config.Save(path);
        var json = JsonDocument.Parse(File.ReadAllText(path));
        var reloaded = LocalConfig.Load(path);

        Assert.True(json.RootElement.GetProperty("onboardingWizardSeen").GetBoolean());
        Assert.True(json.RootElement.GetProperty("gettingStartedDismissed").GetBoolean());
        Assert.True(reloaded.OnboardingWizardSeen);
        Assert.True(reloaded.GettingStartedDismissed);
    }

    [Fact]
    public void LocalConfig_Load_DefaultsGettingStartedFlagsToFalseWhenFieldsAreMissing()
    {
        var path = _root.WriteFile("legacy.json", "{ \"syncRootPath\": \"C:\\\\sync\" }");

        var config = LocalConfig.Load(path);

        Assert.False(config.OnboardingWizardSeen);
        Assert.False(config.GettingStartedDismissed);
    }

    private static void AddProject(LocalConfig config) =>
        config.AddOrUpdate(
            ProjectIdentity.FromRemote("https://github.com/contoso/getting-started.git"),
            "C:\\projects\\sample");
}
