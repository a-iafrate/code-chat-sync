using System.Text.Json;
using CodeChatSync.Core;
using CodeChatSync.Providers.Claude;
using CodeChatSync.Tests.TestSupport;

namespace CodeChatSync.Tests;

public sealed class SkipRunningCheckTests : IDisposable
{
    private const string SessionPath = "session-1/events.jsonl";
    private readonly TempDirectory _root = new();

    public void Dispose() => _root.Dispose();

    [Fact]
    public void SkipRunningCheckProviderIds_DefaultsToEmpty()
    {
        var config = new LocalConfig();

        Assert.Empty(config.SkipRunningCheckProviderIds);
        Assert.Empty(LocalConfig.Load(_root.Combine("missing.json")).SkipRunningCheckProviderIds);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsSkipRunningCheckProviderIds()
    {
        var path = _root.Combine("local-config.json");
        var config = new LocalConfig { SkipRunningCheckProviderIds = ["visualstudio", "claudecode"] };

        config.Save(path);

        using var json = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(
            ["visualstudio", "claudecode"],
            json.RootElement.GetProperty("skipRunningCheckProviderIds").EnumerateArray()
                .Select(element => element.GetString()));
        Assert.Equal(config.SkipRunningCheckProviderIds, LocalConfig.Load(path).SkipRunningCheckProviderIds);
    }

    [Fact]
    public void Load_LegacyJsonWithoutSkipRunningCheckProviderIdsDefaultsToEmpty()
    {
        var path = _root.WriteFile("legacy-config.json", "{ \"syncRootPath\": \"C:\\\\sync\" }");

        var config = LocalConfig.Load(path);

        Assert.Empty(config.SkipRunningCheckProviderIds);
    }

    [Fact]
    public void IsRunningCheckSkipped_MatchesProviderIdsCaseInsensitively()
    {
        var config = new LocalConfig { SkipRunningCheckProviderIds = ["VisualStudio"] };

        Assert.True(config.IsRunningCheckSkipped("visualstudio"));
        Assert.True(config.IsRunningCheckSkipped("VISUALSTUDIO"));
        Assert.False(config.IsRunningCheckSkipped("claudecode"));
    }

    [Fact]
    public void ForSync_ReturnsTheInnerGuardWhenNoProviderIsSkipped()
    {
        var inner = new FakeProcessGuard("devenv");

        var guard = ProcessGuard.ForSync(inner, new LocalConfig(), [CreateProvider("visualstudio", "devenv")]);

        Assert.Same(inner, guard);
    }

    [Fact]
    public void ForSync_FiltersSkippedProviderProcessesAndKeepsOtherProcesses()
    {
        var inner = new FakeProcessGuard("devenv", "claude");
        var providers = new IChatProvider[]
        {
            CreateProvider("visualstudio", "devenv"),
            CreateProvider("claudecode", "claude")
        };
        var config = new LocalConfig { SkipRunningCheckProviderIds = ["VISUALSTUDIO"] };

        var running = ProcessGuard.ForSync(inner, config, providers)
            .GetRunningProcesses(["devenv", "claude"]);

        Assert.Equal(["claude"], running);
    }

    [Fact]
    public void ForSync_RejectsNullArguments()
    {
        var inner = new FakeProcessGuard();
        var config = new LocalConfig();
        IChatProvider[] providers = [];

        Assert.Throws<ArgumentNullException>(() => ProcessGuard.ForSync(null!, config, providers));
        Assert.Throws<ArgumentNullException>(() => ProcessGuard.ForSync(inner, null!, providers));
        Assert.Throws<ArgumentNullException>(() => ProcessGuard.ForSync(inner, config, null!));
    }

    [Fact]
    public void ChatSyncService_PullsLocalFilesWhenTheProviderRunningCheckIsSkipped()
    {
        var (provider, project, localRoot, syncRoot) = CreateSyncFixture();
        WriteSyncFile(syncRoot, provider.Id, project, SessionPath, "remote content");
        var config = new LocalConfig { SkipRunningCheckProviderIds = ["visualstudio"] };
        var guard = ProcessGuard.ForSync(new FakeProcessGuard("devenv"), config, [provider]);

        var report = new ChatSyncService(guard).Sync(provider, project, syncRoot, new SyncState());

        var entry = Assert.Single(report.Entries);
        Assert.Equal(SyncAction.Pulled, entry.Action);
        Assert.Equal("remote content", File.ReadAllText(Path.Combine(localRoot, "session-1", "events.jsonl")));
        Assert.False(report.HasBlockedPulls);
    }

    [Fact]
    public void ChatSyncService_SkipsLocalWritesWhenTheProviderRunningCheckIsNotSkipped()
    {
        var (provider, project, localRoot, syncRoot) = CreateSyncFixture();
        WriteSyncFile(syncRoot, provider.Id, project, SessionPath, "remote content");
        var guard = ProcessGuard.ForSync(new FakeProcessGuard("devenv"), new LocalConfig(), [provider]);

        var report = new ChatSyncService(guard).Sync(provider, project, syncRoot, new SyncState());

        var entry = Assert.Single(report.Entries);
        Assert.Equal(SyncAction.Skipped, entry.Action);
        Assert.True(entry.IsBlockedByProvider);
        Assert.True(report.HasBlockedPulls);
        Assert.False(File.Exists(Path.Combine(localRoot, "session-1", "events.jsonl")));
    }

    [Fact]
    public void ClaudeDiscover_DoesNotThrowWhenItsRunningCheckIsSkipped()
    {
        using var projects = new ClaudeProjectsFixture();
        var localPath = projects.CreateLocalProject("client-app");
        var provider = new ClaudeCodeChatProvider(
            ProcessGuard.ForSync(
                new FakeProcessGuard("claude"),
                new LocalConfig { SkipRunningCheckProviderIds = ["claudecode"] },
                [new ClaudeCodeChatProvider(new FakeProcessGuard(), projects.ProjectsRoot)]),
            projects.ProjectsRoot);

        var locations = provider.Discover(ClaudeProjectsFixture.Project(localPath));

        Assert.Empty(locations);
    }

    [Fact]
    public void ClaudeDiscover_ThrowsWhenItsRunningCheckIsNotSkipped()
    {
        using var projects = new ClaudeProjectsFixture();
        var localPath = projects.CreateLocalProject("client-app");
        var provider = new ClaudeCodeChatProvider(
            ProcessGuard.ForSync(
                new FakeProcessGuard("claude"),
                new LocalConfig(),
                [new ClaudeCodeChatProvider(new FakeProcessGuard(), projects.ProjectsRoot)]),
            projects.ProjectsRoot);

        var exception = Assert.Throws<ClaudeCodeRunningException>(
            () => provider.Discover(ClaudeProjectsFixture.Project(localPath)));

        Assert.Equal(["claude"], exception.RunningProcesses);
    }

    private (FakeChatProvider Provider, ProjectInfo Project, string LocalRoot, string SyncRoot) CreateSyncFixture()
    {
        var localRoot = _root.Combine("local");
        var syncRoot = _root.Combine("sync");
        Directory.CreateDirectory(localRoot);
        Directory.CreateDirectory(syncRoot);
        var provider = new FakeChatProvider(localRoot, "devenv") { Id = "visualstudio" };
        var project = new ProjectInfo
        {
            Identity = ProjectIdentity.FromRemote("https://github.com/example/client-app.git"),
            LocalPath = localRoot
        };

        return (provider, project, localRoot, syncRoot);
    }

    private static void WriteSyncFile(
        string syncRoot,
        string providerId,
        ProjectInfo project,
        string relativePath,
        string content)
    {
        var path = Path.Combine(
            syncRoot,
            providerId,
            project.SyncFolderName,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static FakeChatProvider CreateProvider(string id, params string[] processNames) =>
        new FakeChatProvider(Path.GetTempPath(), processNames) { Id = id };
}
