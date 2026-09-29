using CodeChatSync.Core;
using CodeChatSync.Tests.TestSupport;

namespace CodeChatSync.Tests;

public sealed class SyncOrchestratorExactProjectRemoteTests : IDisposable
{
    private readonly TempDirectory _root = new();

    public void Dispose() => _root.Dispose();

    [Fact]
    public void Run_ExactProjectRemoteDoesNotMatchAnotherRemoteWithTheSamePrefix()
    {
        const string exactRemote = "https://github.com/acme/tool.git";
        const string prefixRemote = "https://github.com/acme/toolkit.git";
        var syncRoot = CreateDirectory("sync");
        var exactProjectPath = CreateDirectory("exact-project");
        var prefixedProjectPath = CreateDirectory("prefixed-project");
        var chatRoot = CreateDirectory("chats");
        File.WriteAllText(Path.Combine(chatRoot, "session.jsonl"), "chat content");

        var exactIdentity = ProjectIdentity.FromRemote(exactRemote);
        var prefixedIdentity = ProjectIdentity.FromRemote(prefixRemote);
        var localConfig = new LocalConfig { SyncRootPath = syncRoot };
        localConfig.AddOrUpdate(exactIdentity, exactProjectPath);
        localConfig.AddOrUpdate(prefixedIdentity, prefixedProjectPath);
        var sharedConfig = new SharedConfig();
        sharedConfig.AddOrUpdate(exactIdentity, "tool");
        sharedConfig.AddOrUpdate(prefixedIdentity, "toolkit");
        var workspace = new SyncWorkspace(localConfig, sharedConfig, _root.Combine("state"));
        var orchestrator = new SyncOrchestrator(
            new FakeChatProvider(chatRoot),
            workspace,
            new ChatSyncService(new FakeProcessGuard()));

        var result = orchestrator.Run(new SyncRunOptions { ExactProjectRemote = exactIdentity.NormalizedRemote });

        var syncedProject = Assert.Single(result.Projects).Project;
        Assert.Equal(exactIdentity.NormalizedRemote, syncedProject.Identity.NormalizedRemote);
        Assert.Equal("tool", syncedProject.SyncFolderName);
        Assert.Equal(1, result.PushedCount);
        Assert.Equal(
            "chat content",
            File.ReadAllText(Path.Combine(syncRoot, "fake", "tool", "session.jsonl")));
        Assert.False(File.Exists(Path.Combine(syncRoot, "fake", "toolkit", "session.jsonl")));
    }

    private string CreateDirectory(string name)
    {
        var path = _root.Combine(name);
        Directory.CreateDirectory(path);
        return path;
    }
}
