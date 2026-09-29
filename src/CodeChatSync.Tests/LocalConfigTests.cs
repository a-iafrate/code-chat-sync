using CodeChatSync.Core;
using CodeChatSync.Tests.TestSupport;

namespace CodeChatSync.Tests;

[Collection(EnvironmentCollection.Name)]
public class LocalConfigTests : IDisposable
{
    private readonly TempDirectory _root = new();

    public void Dispose() => _root.Dispose();

    [Fact]
    public void Load_ReturnsEmptyConfigWhenTheFileIsMissing()
    {
        var config = LocalConfig.Load(_root.Combine("absent.json"));

        Assert.Null(config.SyncRootPath);
        Assert.Empty(config.Projects);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsConfiguration()
    {
        var path = _root.Combine("nested", "local-config.json");
        var config = new LocalConfig { SyncRootPath = _root.Path };
        config.AddOrUpdate(ProjectIdentity.FromRemote("https://github.com/a-iafrate/code-chat-sync.git"), _root.Path);
        config.Save(path);

        var reloaded = LocalConfig.Load(path);

        Assert.Equal(_root.Path, reloaded.SyncRootPath);
        var entry = Assert.Single(reloaded.Projects);
        Assert.Equal("github.com/a-iafrate/code-chat-sync", entry.Remote);
        Assert.Equal(_root.Path, entry.LocalPath);
    }

    [Fact]
    public void Load_ReportsCorruptConfigurationInsteadOfSilentlyDiscardingIt()
    {
        var path = _root.WriteFile("local-config.json", "{ not json");

        Assert.Throws<InvalidDataException>(() => LocalConfig.Load(path));
    }

    [Fact]
    public void AddOrUpdate_RegistersANewProject()
    {
        var config = new LocalConfig();

        var isNew = config.AddOrUpdate(ProjectIdentity.FromRemote("https://github.com/a-iafrate/code-chat-sync.git"), _root.Path);

        Assert.True(isNew);
        Assert.Single(config.Projects);
    }

    [Fact]
    public void AddOrUpdate_UpdatesThePathWhenTheProjectMovedOnThisPc()
    {
        var identity = ProjectIdentity.FromRemote("https://github.com/a-iafrate/code-chat-sync.git");
        var config = new LocalConfig();
        config.AddOrUpdate(identity, _root.Combine("old"));

        var isNew = config.AddOrUpdate(identity, _root.Combine("new"));

        Assert.False(isNew);
        var entry = Assert.Single(config.Projects);
        Assert.Equal(_root.Combine("new"), entry.LocalPath);
    }

    [Fact]
    public void Find_MatchesRegardlessOfHowTheRemoteWasWritten()
    {
        var config = new LocalConfig();
        config.AddOrUpdate(ProjectIdentity.FromRemote("git@github.com:a-iafrate/code-chat-sync.git"), _root.Path);

        var found = config.Find(ProjectIdentity.FromRemote("https://github.com/a-iafrate/code-chat-sync.git"));

        Assert.NotNull(found);
    }

    [Fact]
    public void Find_ReturnsNullForAnUnregisteredProject()
    {
        var config = new LocalConfig();
        config.AddOrUpdate(ProjectIdentity.FromRemote("https://github.com/a-iafrate/code-chat-sync.git"), _root.Path);

        Assert.Null(config.Find(ProjectIdentity.FromRemote("https://github.com/contoso/other.git")));
    }

    [Fact]
    public void DefaultStateDirectory_IsSeparateFromTheConfigFile()
    {
        Assert.NotEqual(LocalConfig.GetDefaultPath(), LocalConfig.GetDefaultStateDirectory());
    }

    [Fact]
    public void DefaultPaths_FollowTheHomeOverrideWhenItIsSet()
    {
        var previous = Environment.GetEnvironmentVariable(LocalConfig.HomeEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(LocalConfig.HomeEnvironmentVariable, _root.Path);

            Assert.Equal(Path.Combine(_root.Path, "local-config.json"), LocalConfig.GetDefaultPath());
            Assert.Equal(Path.Combine(_root.Path, "state"), LocalConfig.GetDefaultStateDirectory());
        }
        finally
        {
            Environment.SetEnvironmentVariable(LocalConfig.HomeEnvironmentVariable, previous);
        }
    }

    [Fact]
    public void DefaultPaths_IgnoreABlankHomeOverride()
    {
        var previous = Environment.GetEnvironmentVariable(LocalConfig.HomeEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(LocalConfig.HomeEnvironmentVariable, "   ");

            Assert.Contains("CodeChatSync", LocalConfig.GetDefaultPath(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LocalConfig.HomeEnvironmentVariable, previous);
        }
    }

    [Fact]
    public void SetRestoreSelection_FindsEquivalentRemoteAndIsolatesProviderAndRemote()
    {
        var config = new LocalConfig();
        var identity = ProjectIdentity.FromRemote("https://github.com/Contoso/Per-PC.git");
        var otherIdentity = ProjectIdentity.FromRemote("https://github.com/contoso/another-project.git");
        config.SetRestoreSelection("visualstudio", identity, ["session-a"]);
        config.SetRestoreSelection("claudecode", identity, ["session-b"]);
        config.SetRestoreSelection("visualstudio", otherIdentity, ["session-c"]);

        var equivalentIdentity = ProjectIdentity.FromRemote("git@github.com:CONTOSO/Per-PC.git");

        Assert.Equal(["session-a"], config.FindRestoreSelection("VISUALSTUDIO", equivalentIdentity)!.SessionIds);
        Assert.Equal(["session-b"], config.FindRestoreSelection("claudecode", identity)!.SessionIds);
        Assert.Equal(["session-c"], config.FindRestoreSelection("visualstudio", otherIdentity)!.SessionIds);
        Assert.Null(config.FindRestoreSelection("another-provider", identity));
        Assert.Null(config.FindRestoreSelection("visualstudio", ProjectIdentity.FromRemote("https://github.com/contoso/missing.git")));
    }

    [Fact]
    public void SetRestoreSelection_ReplacesAndRemovesEntriesWithoutCollapsingEmptySelection()
    {
        var config = new LocalConfig();
        var identity = ProjectIdentity.FromRemote("https://github.com/contoso/restore-states.git");
        config.SetRestoreSelection("visualstudio", identity, ["old-session"]);
        config.SetRestoreSelection("claudecode", identity, ["keep-session"]);

        config.SetRestoreSelection("visualstudio", identity, ["new-session"]);
        Assert.Single(config.RestoreSelections, entry => entry.ProviderId == "visualstudio");
        Assert.Equal(["new-session"], config.FindRestoreSelection("visualstudio", identity)!.SessionIds);

        config.SetRestoreSelection("visualstudio", identity, []);
        var emptySelection = config.FindRestoreSelection("visualstudio", identity);
        Assert.NotNull(emptySelection);
        Assert.Empty(emptySelection.SessionIds);
        Assert.Equal(2, config.RestoreSelections.Count);

        config.SetRestoreSelection("visualstudio", identity, null);
        Assert.Null(config.FindRestoreSelection("visualstudio", identity));
        Assert.Equal(["keep-session"], config.FindRestoreSelection("claudecode", identity)!.SessionIds);
        Assert.Single(config.RestoreSelections);
    }

    [Fact]
    public void SetRestoreSelection_CollapsesCaseInsensitiveDuplicatesAndPreservesStateOnInvalidId()
    {
        var config = new LocalConfig();
        var identity = ProjectIdentity.FromRemote("https://github.com/contoso/validated-selection.git");
        config.SetRestoreSelection("visualstudio", identity, ["previous"]);

        config.SetRestoreSelection("visualstudio", identity, ["session-a", "SESSION-A", "session-b"]);
        Assert.Equal(["session-a", "session-b"], config.FindRestoreSelection("visualstudio", identity)!.SessionIds);

        Assert.Throws<ArgumentException>(() =>
            config.SetRestoreSelection("visualstudio", identity, ["replacement", "bad/session"]));

        Assert.Single(config.RestoreSelections);
        Assert.Equal(["session-a", "session-b"], config.FindRestoreSelection("visualstudio", identity)!.SessionIds);
    }

    [Fact]
    public void SaveAndLoad_PersistsRestoreSelectionsOnlyInPerPcConfiguration()
    {
        var identity = ProjectIdentity.FromRemote("https://github.com/contoso/local-only.git");
        var emptyIdentity = ProjectIdentity.FromRemote("https://github.com/contoso/restore-none.git");
        var config = new LocalConfig { SyncRootPath = _root.Combine("sync") };
        config.AddOrUpdate(identity, _root.Combine("local-project"));
        config.SetRestoreSelection("visualstudio", identity, ["session-a", "session-b"]);
        config.SetRestoreSelection("visualstudio", emptyIdentity, []);
        var configPath = _root.Combine("local-config.json");

        config.Save(configPath);
        var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(configPath));
        var serializedSelections = json.RootElement.GetProperty("restoreSelections");
        var reloaded = LocalConfig.Load(configPath);

        Assert.Equal(2, serializedSelections.GetArrayLength());
        Assert.Equal(config.SyncRootPath, reloaded.SyncRootPath);
        var project = Assert.Single(reloaded.Projects);
        Assert.Equal(identity.NormalizedRemote, project.Remote);
        Assert.Equal(_root.Combine("local-project"), project.LocalPath);
        Assert.Equal(["visualstudio"], project.ProviderIds);
        Assert.Equal(["session-a", "session-b"], reloaded.FindRestoreSelection("visualstudio", identity)!.SessionIds);
        Assert.Empty(reloaded.FindRestoreSelection("visualstudio", emptyIdentity)!.SessionIds);

        var syncRoot = _root.Combine("shared-sync");
        new SharedConfig().Save(syncRoot);
        Assert.DoesNotContain("restoreSelections", File.ReadAllText(SharedConfig.GetPath(syncRoot)), StringComparison.Ordinal);

        var noSelectionPath = _root.Combine("no-selections.json");
        new LocalConfig().Save(noSelectionPath);
        var noSelections = LocalConfig.Load(noSelectionPath);
        Assert.Empty(noSelections.RestoreSelections);
        Assert.Null(noSelections.FindRestoreSelection("visualstudio", identity));
    }

    [Fact]
    public void Load_EnablesAutomaticSyncWhenTheFileIsMissing()
    {
        var config = LocalConfig.Load(_root.Combine("absent.json"));

        Assert.True(config.AutomaticSyncOnProviderClose);
    }

    [Fact]
    public void Load_EnablesAutomaticSyncForLegacyJsonWithoutThePreference()
    {
        var path = _root.WriteFile("local-config.json", """
            {
              "syncRootPath": "C:\\sync",
              "projects": [
                { "remote": "github.com/contoso/legacy", "localPath": "C:\\src\\legacy" }
              ]
            }
            """);

        var config = LocalConfig.Load(path);

        Assert.True(config.AutomaticSyncOnProviderClose);
        Assert.Equal(@"C:\sync", config.SyncRootPath);
        var entry = Assert.Single(config.Projects);
        Assert.Equal("github.com/contoso/legacy", entry.Remote);
        Assert.Empty(config.RestoreSelections);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Load_ReadsAnExplicitAutomaticSyncPreference(bool enabled)
    {
        var path = _root.WriteFile(
            "local-config.json",
            $$"""{ "automaticSyncOnProviderClose": {{(enabled ? "true" : "false")}} }""");

        Assert.Equal(enabled, LocalConfig.Load(path).AutomaticSyncOnProviderClose);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SaveAndLoad_PersistsAutomaticSyncPreference(bool enabled)
    {
        var path = _root.Combine("local-config.json");
        var config = new LocalConfig { SyncRootPath = _root.Path, AutomaticSyncOnProviderClose = enabled };

        config.Save(path);
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var reloaded = LocalConfig.Load(path);

        Assert.Equal(enabled, json.RootElement.GetProperty("automaticSyncOnProviderClose").GetBoolean());
        Assert.Equal(enabled, reloaded.AutomaticSyncOnProviderClose);
        Assert.Equal(_root.Path, reloaded.SyncRootPath);
    }

    [Fact]
    public void AutomaticSyncPreference_TogglesThroughTheHomeOverrideWithoutLosingOtherSettings()
    {
        var previous = Environment.GetEnvironmentVariable(LocalConfig.HomeEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(LocalConfig.HomeEnvironmentVariable, _root.Path);
            var identity = ProjectIdentity.FromRemote("https://github.com/contoso/toggle.git");
            var initial = new LocalConfig { SyncRootPath = _root.Combine("sync") };
            initial.AddOrUpdate(identity, _root.Combine("project"));
            initial.SetRestoreSelection("visualstudio", identity, ["session-a"]);
            initial.Save();

            // Same load-modify-save sequence the app uses to store the per-PC toggle.
            var disabling = LocalConfig.Load();
            disabling.AutomaticSyncOnProviderClose = false;
            disabling.Save();
            var disabled = LocalConfig.Load();

            Assert.True(File.Exists(Path.Combine(_root.Path, "local-config.json")));
            Assert.False(disabled.AutomaticSyncOnProviderClose);
            Assert.Equal(_root.Combine("sync"), disabled.SyncRootPath);
            Assert.Equal(_root.Combine("project"), Assert.Single(disabled.Projects).LocalPath);
            Assert.Equal(["session-a"], disabled.FindRestoreSelection("visualstudio", identity)!.SessionIds);

            disabled.AutomaticSyncOnProviderClose = true;
            disabled.Save();

            Assert.True(LocalConfig.Load().AutomaticSyncOnProviderClose);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LocalConfig.HomeEnvironmentVariable, previous);
        }
    }

    [Fact]
    public void Load_LegacyJsonWithoutThemePreference_DefaultsToSystem()
    {
        var path = _root.WriteFile("legacy-config.json", """
            {
              "syncRootPath": "C:\\sync",
              "projects": [
                { "remote": "github.com/contoso/legacy-theme", "localPath": "C:\\src\\legacy" }
              ]
            }
            """);

        var config = LocalConfig.Load(path);

        Assert.Equal(ThemePreference.System, config.ThemePreference);
        Assert.Equal(@"C:\sync", config.SyncRootPath);
        var project = Assert.Single(config.Projects);
        Assert.Equal("github.com/contoso/legacy-theme", project.Remote);
        Assert.Equal(@"C:\src\legacy", project.LocalPath);
    }

    [Theory]
    [InlineData(ThemePreference.System, "System")]
    [InlineData(ThemePreference.Light, "Light")]
    [InlineData(ThemePreference.Dark, "Dark")]
    public void SaveAndLoad_PersistsThemePreferenceAsReadableStringWithoutLosingOtherPerPcSettings(
        ThemePreference preference,
        string serializedPreference)
    {
        var identity = ProjectIdentity.FromRemote("https://github.com/contoso/theme-preference.git");
        var config = new LocalConfig
        {
            SyncRootPath = _root.Combine("sync"),
            AutomaticSyncOnProviderClose = false,
            ThemePreference = preference
        };
        config.AddOrUpdate(identity, _root.Combine("project"));
        config.SetRestoreSelection("visualstudio", identity, ["session-a"]);
        var path = _root.Combine("local-config.json");

        config.Save(path);
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var serializedTheme = json.RootElement.GetProperty("themePreference");
        var reloaded = LocalConfig.Load(path);

        Assert.Equal(System.Text.Json.JsonValueKind.String, serializedTheme.ValueKind);
        Assert.Equal(serializedPreference, serializedTheme.GetString());
        Assert.Equal(preference, reloaded.ThemePreference);
        Assert.Equal(_root.Combine("sync"), reloaded.SyncRootPath);
        Assert.False(reloaded.AutomaticSyncOnProviderClose);
        var project = Assert.Single(reloaded.Projects);
        Assert.Equal(identity.NormalizedRemote, project.Remote);
        Assert.Equal(_root.Combine("project"), project.LocalPath);
        Assert.Equal(["session-a"], reloaded.FindRestoreSelection("visualstudio", identity)!.SessionIds);
    }

    [Fact]
    public void Load_RejectsAnUnknownThemePreference()
    {
        var path = _root.WriteFile("invalid-theme-config.json", """
            { "themePreference": "Sepia" }
            """);

        Assert.Throws<InvalidDataException>(() => LocalConfig.Load(path));
    }
}
