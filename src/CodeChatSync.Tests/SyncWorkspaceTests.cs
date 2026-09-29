using CodeChatSync.Core;
using CodeChatSync.Tests.TestSupport;

namespace CodeChatSync.Tests;

[Collection(EnvironmentCollection.Name)]
public class SyncWorkspaceTests : IDisposable
{
    private const string Remote = "https://github.com/a-iafrate/code-chat-sync.git";

    private readonly TempDirectory _root = new();

    public void Dispose() => _root.Dispose();

    [Fact]
    public void ResolveProjects_TakesTheFolderNameFromTheSharedMapping()
    {
        var projectPath = CreateDirectory("repo");
        var localConfig = new LocalConfig();
        localConfig.AddOrUpdate(ProjectIdentity.FromRemote(Remote), projectPath);
        var sharedConfig = new SharedConfig();
        sharedConfig.AddOrUpdate(ProjectIdentity.FromRemote(Remote), "client-erp");

        var resolution = new SyncWorkspace(localConfig, sharedConfig).ResolveProjects();

        var project = Assert.Single(resolution.Projects);
        Assert.Equal("client-erp", project.SyncFolderName);
        Assert.Equal(projectPath, project.LocalPath);
        Assert.Empty(resolution.Unresolved);
    }

    [Fact]
    public void ResolveProjects_FallsBackToTheSlugWhenTheProjectIsNotMapped()
    {
        var projectPath = CreateDirectory("repo");
        var localConfig = new LocalConfig();
        localConfig.AddOrUpdate(ProjectIdentity.FromRemote(Remote), projectPath);

        var resolution = new SyncWorkspace(localConfig, new SharedConfig()).ResolveProjects();

        var project = Assert.Single(resolution.Projects);
        Assert.Equal("a-iafrate-code-chat-sync", project.SyncFolderName);
    }

    [Fact]
    public void ResolveProjects_ReportsProjectsMissingFromThisPcInsteadOfSyncingThem()
    {
        var localConfig = new LocalConfig();
        localConfig.AddOrUpdate(ProjectIdentity.FromRemote(Remote), _root.Combine("never-cloned"));

        var resolution = new SyncWorkspace(localConfig, new SharedConfig()).ResolveProjects();

        Assert.Empty(resolution.Projects);
        var unresolved = Assert.Single(resolution.Unresolved);
        Assert.Contains("does not exist", unresolved.Reason);
    }

    [Fact]
    public void ResolveProjects_ReportsUnparsableRemotes()
    {
        var localConfig = new LocalConfig
        {
            Projects = [new LocalProjectEntry { Remote = "   ", LocalPath = CreateDirectory("repo") }]
        };

        var resolution = new SyncWorkspace(localConfig, new SharedConfig()).ResolveProjects();

        Assert.Empty(resolution.Projects);
        Assert.Single(resolution.Unresolved);
    }

    [Fact]
    public void GetStatePath_IsSeparatePerProviderAndProject()
    {
        var workspace = new SyncWorkspace(new LocalConfig(), new SharedConfig(), _root.Path);
        var first = CreateProject(Remote);
        var second = CreateProject("https://github.com/contoso/other.git");

        Assert.NotEqual(
            workspace.GetStatePath("visualstudio", first),
            workspace.GetStatePath("visualstudio", second));

        Assert.NotEqual(
            workspace.GetStatePath("visualstudio", first),
            workspace.GetStatePath("claudecode", first));
    }

    [Fact]
    public void GetStatePath_StaysOutsideTheSyncFolder()
    {
        var syncRoot = CreateDirectory("sync");
        var localConfig = new LocalConfig { SyncRootPath = syncRoot };
        var workspace = new SyncWorkspace(localConfig, new SharedConfig(), _root.Combine("state"));

        var statePath = workspace.GetStatePath("visualstudio", CreateProject(Remote));

        Assert.DoesNotContain(syncRoot, statePath, StringComparison.OrdinalIgnoreCase);
    }

    private static ProjectInfo CreateProject(string remote) => new()
    {
        Identity = ProjectIdentity.FromRemote(remote),
        LocalPath = Path.GetTempPath()
    };

    private string CreateDirectory(string name)
    {
        var path = _root.Combine(name);
        Directory.CreateDirectory(path);
        return path;
    }
}
