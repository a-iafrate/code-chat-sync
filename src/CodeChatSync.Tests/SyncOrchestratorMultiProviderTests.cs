using CodeChatSync.Core;
using CodeChatSync.Tests.TestSupport;

namespace CodeChatSync.Tests;

/// <summary>
/// Multi-provider behavior of <see cref="SyncOrchestrator"/>: provider selection per
/// project, independent baselines and restore selections, and a single publish cycle.
/// Uses synthetic providers only, never real Visual Studio or Claude Code data.
/// </summary>
public sealed class SyncOrchestratorMultiProviderTests : IDisposable
{
    private const string VisualStudio = "visualstudio";
    private const string ClaudeCode = "claudecode";
    private const string HttpsRemote = "https://GitHub.com/Acme/Portal.git";
    private const string SshRemote = "git@github.com:acme/portal.git";
    private const string OtherRemote = "https://github.com/acme/billing.git";

    private readonly TempDirectory _root = new();

    public void Dispose() => _root.Dispose();

    [Fact]
    public void Run_LegacyEntryWithoutProviderIds_SyncsVisualStudioOnly()
    {
        var syncRoot = CreateDirectory("sync");
        var projectPath = CreateDirectory("portal");
        var configPath = _root.Combine("pc", "local-config.json");
        _root.WriteFile("pc/local-config.json", $$"""
            {
              "syncRootPath": {{System.Text.Json.JsonSerializer.Serialize(syncRoot)}},
              "projects": [
                { "remote": "github.com/acme/portal", "localPath": {{System.Text.Json.JsonSerializer.Serialize(projectPath)}} }
              ]
            }
            """);
        var localConfig = LocalConfig.Load(configPath);
        var (vs, claude) = CreateProviders();
        _root.WriteFile("vs-chats/vs-1/events.jsonl", "vs transcript");
        _root.WriteFile("claude-chats/cl-1/events.jsonl", "claude transcript");
        var identity = ProjectIdentity.FromRemote(HttpsRemote);

        var result = CreateOrchestrator(localConfig, [vs, claude]).Run();

        Assert.Null(Assert.Single(localConfig.Projects).ProviderIds);
        Assert.Equal([VisualStudio], LocalConfig.GetEnabledProviderIds(localConfig.Projects[0]));
        var synced = Assert.Single(result.Projects);
        Assert.Equal(VisualStudio, synced.ProviderId);
        Assert.Equal(1, result.PushedCount);
        Assert.Equal("vs transcript", File.ReadAllText(Path.Combine(syncRoot, VisualStudio, identity.Slug, "vs-1", "events.jsonl")));
        Assert.False(Directory.Exists(Path.Combine(syncRoot, ClaudeCode)));
        Assert.False(File.Exists(StatePath(ClaudeCode, identity)));
    }

    [Fact]
    public void Run_ClaudeOnlyEntry_SyncsClaudeOnly()
    {
        var syncRoot = CreateDirectory("sync");
        var projectPath = CreateDirectory("portal");
        var identity = ProjectIdentity.FromRemote(HttpsRemote);
        var localConfig = new LocalConfig { SyncRootPath = syncRoot };
        localConfig.AddOrUpdate(identity, projectPath, ClaudeCode);
        var (vs, claude) = CreateProviders();
        _root.WriteFile("vs-chats/vs-1/events.jsonl", "vs transcript");
        _root.WriteFile("claude-chats/cl-1/events.jsonl", "claude transcript");

        var result = CreateOrchestrator(localConfig, [vs, claude]).Run();

        Assert.Equal([ClaudeCode], Assert.Single(localConfig.Projects).ProviderIds);
        var synced = Assert.Single(result.Projects);
        Assert.Equal(ClaudeCode, synced.ProviderId);
        Assert.Equal("claude transcript", File.ReadAllText(Path.Combine(syncRoot, ClaudeCode, identity.Slug, "cl-1", "events.jsonl")));
        Assert.False(Directory.Exists(Path.Combine(syncRoot, VisualStudio)));
        Assert.True(File.Exists(StatePath(ClaudeCode, identity)));
        Assert.False(File.Exists(StatePath(VisualStudio, identity)));
    }

    [Fact]
    public void Run_DualProviderSameNormalizedRemote_UsesOneEntryAndIndependentBaselines()
    {
        var syncRoot = CreateDirectory("sync");
        var projectPath = CreateDirectory("portal");
        var localConfig = new LocalConfig { SyncRootPath = syncRoot };
        Assert.True(localConfig.AddOrUpdate(ProjectIdentity.FromRemote(HttpsRemote), projectPath, VisualStudio));
        Assert.False(localConfig.AddOrUpdate(ProjectIdentity.FromRemote(SshRemote), projectPath, ClaudeCode));
        var identity = ProjectIdentity.FromRemote(SshRemote);
        var (vs, claude) = CreateProviders();
        _root.WriteFile("vs-chats/vs-1/events.jsonl", "vs transcript");
        _root.WriteFile("claude-chats/cl-1/events.jsonl", "claude transcript");

        var first = CreateOrchestrator(localConfig, [vs, claude]).Run();

        var entry = Assert.Single(localConfig.Projects);
        Assert.Equal("github.com/acme/portal", entry.Remote);
        Assert.Equal([VisualStudio, ClaudeCode], entry.ProviderIds);
        Assert.Equal([VisualStudio, ClaudeCode], first.Projects.Select(project => project.ProviderId));
        Assert.All(first.Projects, project => Assert.Equal(1, project.Report.PushedCount));
        Assert.Equal("vs transcript", File.ReadAllText(Path.Combine(syncRoot, VisualStudio, identity.Slug, "vs-1", "events.jsonl")));
        Assert.Equal("claude transcript", File.ReadAllText(Path.Combine(syncRoot, ClaudeCode, identity.Slug, "cl-1", "events.jsonl")));

        var vsState = SyncState.Load(StatePath(VisualStudio, identity));
        var claudeState = SyncState.Load(StatePath(ClaudeCode, identity));
        Assert.NotNull(vsState.GetBaseline("vs-1/events.jsonl"));
        Assert.Null(vsState.GetBaseline("cl-1/events.jsonl"));
        Assert.NotNull(claudeState.GetBaseline("cl-1/events.jsonl"));
        Assert.Null(claudeState.GetBaseline("vs-1/events.jsonl"));

        _root.WriteFile("claude-chats/cl-2/events.jsonl", "second claude transcript");
        var second = CreateOrchestrator(localConfig, [vs, claude]).Run();

        var vsSecond = Assert.Single(second.Projects, project => project.ProviderId == VisualStudio);
        var claudeSecond = Assert.Single(second.Projects, project => project.ProviderId == ClaudeCode);
        Assert.Equal(0, vsSecond.Report.PushedCount);
        Assert.Equal(1, vsSecond.Report.UnchangedCount);
        Assert.Equal(1, claudeSecond.Report.PushedCount);
        Assert.Equal(1, claudeSecond.Report.UnchangedCount);
        Assert.False(File.Exists(Path.Combine(syncRoot, VisualStudio, identity.Slug, "cl-2", "events.jsonl")));
    }

    [Fact]
    public void Run_DualProvider_AppliesRestoreSelectionPerProvider()
    {
        var syncRoot = CreateDirectory("sync");
        var projectPath = CreateDirectory("portal");
        var identity = ProjectIdentity.FromRemote(HttpsRemote);
        var localConfig = new LocalConfig { SyncRootPath = syncRoot };
        localConfig.AddOrUpdate(identity, projectPath, VisualStudio);
        localConfig.AddOrUpdate(ProjectIdentity.FromRemote(SshRemote), projectPath, ClaudeCode);
        localConfig.SetRestoreSelection(VisualStudio, identity, ["s1"]);
        foreach (var provider in new[] { VisualStudio, ClaudeCode })
        {
            _root.WriteFile($"sync/{provider}/{identity.Slug}/s1/events.jsonl", $"{provider} s1");
            _root.WriteFile($"sync/{provider}/{identity.Slug}/s2/events.jsonl", $"{provider} s2");
        }

        var (vs, claude) = CreateProviders();

        var result = CreateOrchestrator(localConfig, [vs, claude]).Run();

        Assert.Equal("visualstudio s1", File.ReadAllText(_root.Combine("vs-chats", "s1", "events.jsonl")));
        Assert.False(File.Exists(_root.Combine("vs-chats", "s2", "events.jsonl")));
        Assert.Equal("claudecode s1", File.ReadAllText(_root.Combine("claude-chats", "s1", "events.jsonl")));
        Assert.Equal("claudecode s2", File.ReadAllText(_root.Combine("claude-chats", "s2", "events.jsonl")));
        var vsReport = Assert.Single(result.Projects, project => project.ProviderId == VisualStudio).Report;
        var claudeReport = Assert.Single(result.Projects, project => project.ProviderId == ClaudeCode).Report;
        Assert.Equal(1, vsReport.PulledCount);
        Assert.Equal(2, claudeReport.PulledCount);
        Assert.Contains(vsReport.Entries, entry => entry.RelativePath == "s2/events.jsonl" && entry.Action == SyncAction.Skipped);
    }

    [Fact]
    public void Run_DualProvider_EmptyClaudeSelectionRestoresNothingWhileVisualStudioRestoresAll()
    {
        var syncRoot = CreateDirectory("sync");
        var projectPath = CreateDirectory("portal");
        var identity = ProjectIdentity.FromRemote(HttpsRemote);
        var localConfig = new LocalConfig { SyncRootPath = syncRoot };
        localConfig.AddOrUpdate(identity, projectPath, VisualStudio);
        localConfig.AddOrUpdate(identity, projectPath, ClaudeCode);
        localConfig.SetRestoreSelection(ClaudeCode, ProjectIdentity.FromRemote(SshRemote), []);
        foreach (var provider in new[] { VisualStudio, ClaudeCode })
        {
            _root.WriteFile($"sync/{provider}/{identity.Slug}/s1/events.jsonl", $"{provider} s1");
        }

        var (vs, claude) = CreateProviders();

        var result = CreateOrchestrator(localConfig, [vs, claude]).Run();

        Assert.Null(localConfig.FindRestoreSelection(VisualStudio, identity));
        Assert.Empty(localConfig.FindRestoreSelection(ClaudeCode, identity)!.SessionIds);
        Assert.Equal("visualstudio s1", File.ReadAllText(_root.Combine("vs-chats", "s1", "events.jsonl")));
        Assert.False(File.Exists(_root.Combine("claude-chats", "s1", "events.jsonl")));
        Assert.Equal(1, result.PulledCount);
    }

    [Fact]
    public void Run_DualProvider_PreparesAndPublishesExactlyOnceWithCombinedPushCount()
    {
        var syncRoot = CreateDirectory("sync");
        var projectPath = CreateDirectory("portal");
        var identity = ProjectIdentity.FromRemote(HttpsRemote);
        var localConfig = new LocalConfig { SyncRootPath = syncRoot };
        localConfig.AddOrUpdate(identity, projectPath, VisualStudio);
        localConfig.AddOrUpdate(identity, projectPath, ClaudeCode);
        _root.WriteFile("vs-chats/vs-1/events.jsonl", "vs transcript");
        _root.WriteFile("claude-chats/cl-1/events.jsonl", "claude transcript");
        _root.WriteFile("claude-chats/cl-2/events.jsonl", "claude transcript 2");
        var (vs, claude) = CreateProviders();
        var publisher = new CountingPublisher();

        var result = CreateOrchestrator(localConfig, [vs, claude], publisher).Run();

        Assert.Equal(1, publisher.PrepareCalls);
        Assert.Equal([3], publisher.PublishedCounts);
        Assert.Equal(3, result.PushedCount);
        Assert.Equal("Pulled.", result.PrepareMessage);
        Assert.Equal("Published.", result.PublishMessage);
    }

    [Fact]
    public void Run_DualProvider_AbortedPrepareTouchesNeitherProvider()
    {
        var syncRoot = CreateDirectory("sync");
        var projectPath = CreateDirectory("portal");
        var identity = ProjectIdentity.FromRemote(HttpsRemote);
        var localConfig = new LocalConfig { SyncRootPath = syncRoot };
        localConfig.AddOrUpdate(identity, projectPath, VisualStudio);
        localConfig.AddOrUpdate(identity, projectPath, ClaudeCode);
        _root.WriteFile("vs-chats/vs-1/events.jsonl", "vs transcript");
        _root.WriteFile("claude-chats/cl-1/events.jsonl", "claude transcript");
        var (vs, claude) = CreateProviders();
        var publisher = new CountingPublisher { PrepareResult = SyncPublishResult.Stop("Divergent history.") };

        var result = CreateOrchestrator(localConfig, [vs, claude], publisher).Run();

        Assert.Equal("Divergent history.", result.AbortReason);
        Assert.Equal(1, publisher.PrepareCalls);
        Assert.Empty(publisher.PublishedCounts);
        Assert.Empty(result.Projects);
        Assert.False(Directory.Exists(Path.Combine(syncRoot, VisualStudio)));
        Assert.False(Directory.Exists(Path.Combine(syncRoot, ClaudeCode)));
    }

    [Fact]
    public void Run_ProjectFilter_SyncsBothProvidersOfTheMatchingProjectOnly()
    {
        var syncRoot = CreateDirectory("sync");
        var portalPath = CreateDirectory("portal");
        var billingPath = CreateDirectory("billing");
        var portal = ProjectIdentity.FromRemote(HttpsRemote);
        var billing = ProjectIdentity.FromRemote(OtherRemote);
        var localConfig = new LocalConfig { SyncRootPath = syncRoot };
        localConfig.AddOrUpdate(portal, portalPath, VisualStudio);
        localConfig.AddOrUpdate(portal, portalPath, ClaudeCode);
        localConfig.AddOrUpdate(billing, billingPath, VisualStudio);
        localConfig.AddOrUpdate(billing, billingPath, ClaudeCode);
        _root.WriteFile("vs-chats/vs-1/events.jsonl", "vs transcript");
        _root.WriteFile("claude-chats/cl-1/events.jsonl", "claude transcript");
        var (vs, claude) = CreateProviders();
        var publisher = new CountingPublisher();

        var result = CreateOrchestrator(localConfig, [vs, claude], publisher).Run(new SyncRunOptions { ProjectFilter = "billing" });

        Assert.Equal(2, result.Projects.Count);
        Assert.All(result.Projects, project => Assert.Equal(billing.NormalizedRemote, project.Project.Identity.NormalizedRemote));
        Assert.Equal([VisualStudio, ClaudeCode], result.Projects.Select(project => project.ProviderId));
        Assert.True(File.Exists(Path.Combine(syncRoot, VisualStudio, billing.Slug, "vs-1", "events.jsonl")));
        Assert.True(File.Exists(Path.Combine(syncRoot, ClaudeCode, billing.Slug, "cl-1", "events.jsonl")));
        Assert.False(Directory.Exists(Path.Combine(syncRoot, VisualStudio, portal.Slug)));
        Assert.False(Directory.Exists(Path.Combine(syncRoot, ClaudeCode, portal.Slug)));
        Assert.False(File.Exists(StatePath(VisualStudio, portal)));
        Assert.False(File.Exists(StatePath(ClaudeCode, portal)));
        Assert.Equal(1, publisher.PrepareCalls);
        Assert.Equal([2], publisher.PublishedCounts);
    }

    [Fact]
    public void Run_ExactProjectRemote_RespectsEachProjectsEnabledProviders()
    {
        var syncRoot = CreateDirectory("sync");
        var portalPath = CreateDirectory("portal");
        var billingPath = CreateDirectory("billing");
        var portal = ProjectIdentity.FromRemote(HttpsRemote);
        var billing = ProjectIdentity.FromRemote(OtherRemote);
        var localConfig = new LocalConfig { SyncRootPath = syncRoot };
        localConfig.AddOrUpdate(portal, portalPath, ClaudeCode);
        localConfig.AddOrUpdate(billing, billingPath, VisualStudio);
        _root.WriteFile("vs-chats/vs-1/events.jsonl", "vs transcript");
        _root.WriteFile("claude-chats/cl-1/events.jsonl", "claude transcript");
        var (vs, claude) = CreateProviders();

        var result = CreateOrchestrator(localConfig, [vs, claude])
            .Run(new SyncRunOptions { ExactProjectRemote = portal.NormalizedRemote });

        var synced = Assert.Single(result.Projects);
        Assert.Equal(ClaudeCode, synced.ProviderId);
        Assert.Equal(portal.NormalizedRemote, synced.Project.Identity.NormalizedRemote);
        Assert.True(File.Exists(Path.Combine(syncRoot, ClaudeCode, portal.Slug, "cl-1", "events.jsonl")));
        Assert.False(Directory.Exists(Path.Combine(syncRoot, VisualStudio)));
    }

    private (SessionFakeProvider VisualStudio, SessionFakeProvider Claude) CreateProviders() =>
        (new SessionFakeProvider(VisualStudio, CreateDirectory("vs-chats"), "devenv"),
         new SessionFakeProvider(ClaudeCode, CreateDirectory("claude-chats"), "claude"));

    private SyncOrchestrator CreateOrchestrator(
        LocalConfig localConfig,
        IEnumerable<IChatProvider> providers,
        ISyncPublisher? publisher = null) =>
        new(
            providers,
            new SyncWorkspace(localConfig, new SharedConfig(), _root.Combine("state")),
            new ChatSyncService(new FakeProcessGuard()),
            publisher);

    private string StatePath(string providerId, ProjectIdentity identity) =>
        _root.Combine("state", providerId, $"{identity.Slug}.json");

    private string CreateDirectory(string name) => Directory.CreateDirectory(_root.Combine(name)).FullName;

    /// <summary>Synthetic provider whose first path segment is the session ID.</summary>
    private sealed class SessionFakeProvider(string id, string localRoot, params string[] processNames) : IChatSessionProvider
    {
        private readonly FakeChatProvider _inner = new(localRoot, processNames) { Id = id };

        public string Id => id;

        public IReadOnlyList<string> ProcessNames => _inner.ProcessNames;

        public IEnumerable<ChatLocation> Discover(ProjectInfo project) => _inner.Discover(project);

        public string MapToLocal(ProjectInfo project, string relativePath) => _inner.MapToLocal(project, relativePath);

        public string GetSessionId(string relativePath) => relativePath.Split('/', '\\')[0];
    }

    /// <summary>Counts how the orchestrator drives publishing, without using Git.</summary>
    private sealed class CountingPublisher : ISyncPublisher
    {
        public SyncPublishResult PrepareResult { get; init; } = SyncPublishResult.Ok("Pulled.");

        public int PrepareCalls { get; private set; }

        public List<int> PublishedCounts { get; } = [];

        public SyncPublishResult PrepareForSync()
        {
            PrepareCalls++;
            return PrepareResult;
        }

        public SyncPublishResult PublishChanges(int pushedCount)
        {
            PublishedCounts.Add(pushedCount);
            return SyncPublishResult.Ok("Published.");
        }
    }
}