using CodeChatSync.Core;
using CodeChatSync.Git;
using CodeChatSync.Tests.TestSupport;

namespace CodeChatSync.Tests;

public sealed class ProjectRegistrationTests : IDisposable
{
    private const string PrimaryRemote = "https://github.com/acme/primary.git";
    private const string OtherRemote = "https://github.com/acme/other.git";

    private readonly TempDirectory _root = new();
    private readonly GitCommandRunner _git = new();
    private string ConfigPath => _root.Combine("pc", "local-config.json");

    public void Dispose() => _root.Dispose();

    [Fact]
    public void Add_RegistersRepositoryRootByDetectedOriginAndPersistsSharedMapping()
    {
        var syncRoot = CreateDirectory("sync");
        var repositoryRoot = CreateRepository("client", PrimaryRemote);
        var nestedPath = Directory.CreateDirectory(Path.Combine(repositoryRoot, "src", "module")).FullName;
        SaveConfig(syncRoot);

        var project = ProjectRegistration.Add(nestedPath, "client-portal", configPath: ConfigPath);

        Assert.Equal(Path.GetFullPath(repositoryRoot), project.LocalPath);
        Assert.Equal("github.com/acme/primary", project.Identity.NormalizedRemote);
        Assert.Equal("client-portal", project.DisplayName);

        var local = LocalConfig.Load(ConfigPath);
        var localEntry = Assert.Single(local.Projects);
        Assert.Equal(project.Identity.NormalizedRemote, localEntry.Remote);
        Assert.Equal(Path.GetFullPath(repositoryRoot), localEntry.LocalPath);

        var shared = SharedConfig.Load(syncRoot);
        var sharedEntry = Assert.Single(shared.Projects);
        Assert.Equal(project.Identity.NormalizedRemote, sharedEntry.Remote);
        Assert.Equal("client-portal", sharedEntry.Name);
    }

    [Fact]
    public void Add_UsesRemoteOverrideAndOriginalPathWhenNoGitRepositoryExists()
    {
        var syncRoot = CreateDirectory("sync");
        var projectPath = CreateDirectory("standalone");
        SaveConfig(syncRoot);

        var project = ProjectRegistration.Add(
            projectPath,
            name: "standalone-project",
            remoteOverride: "git@github.com:acme/standalone.git",
            configPath: ConfigPath);

        Assert.Equal(Path.GetFullPath(projectPath), project.LocalPath);
        Assert.Equal("github.com/acme/standalone", project.Identity.NormalizedRemote);
        Assert.Equal("standalone-project", Assert.Single(SharedConfig.Load(syncRoot).Projects).Name);
        Assert.Equal(Path.GetFullPath(projectPath), Assert.Single(LocalConfig.Load(ConfigPath).Projects).LocalPath);
    }

    [Fact]
    public void Add_ReAddAtMovedPathPreservesIdentityAndSharedName()
    {
        var syncRoot = CreateDirectory("sync");
        var oldPath = CreateRepository("old-location", PrimaryRemote);
        SaveConfig(syncRoot);
        var original = ProjectRegistration.Add(oldPath, "stable-folder", configPath: ConfigPath);
        var movedPath = CreateRepository("new-location", PrimaryRemote);

        var moved = ProjectRegistration.Add(movedPath, configPath: ConfigPath);

        Assert.Equal(original.Identity, moved.Identity);
        Assert.Equal(Path.GetFullPath(movedPath), moved.LocalPath);
        Assert.Equal("stable-folder", moved.DisplayName);

        var localEntry = Assert.Single(LocalConfig.Load(ConfigPath).Projects);
        Assert.Equal(original.Identity.NormalizedRemote, localEntry.Remote);
        Assert.Equal(Path.GetFullPath(movedPath), localEntry.LocalPath);
        var sharedEntry = Assert.Single(SharedConfig.Load(syncRoot).Projects);
        Assert.Equal(original.Identity.NormalizedRemote, sharedEntry.Remote);
        Assert.Equal("stable-folder", sharedEntry.Name);
    }

    [Fact]
    public void Remove_LeavesSharedMappingAndArchivedFilesIntact()
    {
        var syncRoot = CreateDirectory("sync");
        var projectPath = CreateRepository("client", PrimaryRemote);
        SaveConfig(syncRoot);
        var project = ProjectRegistration.Add(projectPath, "client-portal", configPath: ConfigPath);
        var archive = _root.WriteFile("sync/visualstudio/client-portal/session/events.jsonl", "archived transcript");

        ProjectRegistration.Remove(project.Identity, ConfigPath);

        Assert.Empty(LocalConfig.Load(ConfigPath).Projects);
        var sharedEntry = Assert.Single(SharedConfig.Load(syncRoot).Projects);
        Assert.Equal(project.Identity.NormalizedRemote, sharedEntry.Remote);
        Assert.Equal("client-portal", sharedEntry.Name);
        Assert.Equal("archived transcript", File.ReadAllText(archive));
    }

    [Fact]
    public void Add_RejectsSyncFolderInsideClientRepositoryWithoutChangingConfiguration()
    {
        var repositoryRoot = CreateRepository("client", PrimaryRemote);
        var syncRoot = Directory.CreateDirectory(Path.Combine(repositoryRoot, ".codechatsync")).FullName;
        var (localConfigFile, sharedConfigFile) = SaveConfig(syncRoot);
        var localBefore = File.ReadAllBytes(localConfigFile);
        var sharedBefore = File.ReadAllBytes(sharedConfigFile);

        Assert.Throws<ArgumentException>(() => ProjectRegistration.Add(repositoryRoot, configPath: ConfigPath));

        AssertConfigUnchanged(localConfigFile, localBefore, sharedConfigFile, sharedBefore);
    }

    [Fact]
    public void Add_RejectsClientRepositoryInsideSyncFolderWithoutChangingConfiguration()
    {
        var syncRoot = CreateDirectory("sync");
        var projectPath = CreateRepository(Path.Combine("sync", "client"), PrimaryRemote);
        var (localConfigFile, sharedConfigFile) = SaveConfig(syncRoot);
        var localBefore = File.ReadAllBytes(localConfigFile);
        var sharedBefore = File.ReadAllBytes(sharedConfigFile);

        Assert.Throws<ArgumentException>(() => ProjectRegistration.Add(projectPath, configPath: ConfigPath));

        AssertConfigUnchanged(localConfigFile, localBefore, sharedConfigFile, sharedBefore);
    }

    [Fact]
    public void Add_RejectsDuplicateSharedNameWithoutChangingConfiguration()
    {
        var syncRoot = CreateDirectory("sync");
        var firstPath = CreateRepository("first", PrimaryRemote);
        var secondPath = CreateRepository("second", OtherRemote);
        var shared = new SharedConfig();
        shared.AddOrUpdate(ProjectIdentity.FromRemote(PrimaryRemote), "shared-name");
        var (localConfigFile, sharedConfigFile) = SaveConfig(syncRoot, shared);
        var localBefore = File.ReadAllBytes(localConfigFile);
        var sharedBefore = File.ReadAllBytes(sharedConfigFile);

        Assert.Throws<ArgumentException>(() => ProjectRegistration.Add(
            secondPath,
            name: "shared-name",
            configPath: ConfigPath));

        AssertConfigUnchanged(localConfigFile, localBefore, sharedConfigFile, sharedBefore);
        Assert.Equal(Path.GetFullPath(firstPath), firstPath);
    }

    [Fact]
    public void Add_RejectsMissingRemoteWithoutChangingConfiguration()
    {
        var syncRoot = CreateDirectory("sync");
        var projectPath = CreateDirectory("no-remote");
        var (localConfigFile, sharedConfigFile) = SaveConfig(syncRoot);
        var localBefore = File.ReadAllBytes(localConfigFile);
        var sharedBefore = File.ReadAllBytes(sharedConfigFile);

        Assert.Throws<InvalidOperationException>(() => ProjectRegistration.Add(projectPath, configPath: ConfigPath));

        AssertConfigUnchanged(localConfigFile, localBefore, sharedConfigFile, sharedBefore);
    }

    [Fact]
    public void Add_RejectsCredentialBearingRemoteOverrideWithoutChangingConfiguration()
    {
        var syncRoot = CreateDirectory("sync");
        var projectPath = CreateDirectory("standalone");
        var (localConfigFile, sharedConfigFile) = SaveConfig(syncRoot);
        var localBefore = File.ReadAllBytes(localConfigFile);
        var sharedBefore = File.ReadAllBytes(sharedConfigFile);

        Assert.Throws<ArgumentException>(() => ProjectRegistration.Add(
            projectPath,
            remoteOverride: "https://user:password@example.invalid/acme/project.git",
            configPath: ConfigPath));

        AssertConfigUnchanged(localConfigFile, localBefore, sharedConfigFile, sharedBefore);
    }

    private string CreateDirectory(string relativePath)
    {
        var path = Path.Combine(_root.Path, relativePath);
        Directory.CreateDirectory(path);
        return path;
    }

    private string CreateRepository(string relativePath, string remote)
    {
        var path = CreateDirectory(relativePath);
        _git.RunOrThrow(path, ["init"]);
        _git.RunOrThrow(path, ["remote", "add", "origin", remote]);
        return path;
    }

    private (string LocalConfigFile, string SharedConfigFile) SaveConfig(string syncRoot, SharedConfig? shared = null)
    {
        new LocalConfig { SyncRootPath = syncRoot }.Save(ConfigPath);
        var local = LocalConfig.GetDefaultPath();
        (shared ?? new SharedConfig()).Save(syncRoot);
        return (ConfigPath, SharedConfig.GetPath(syncRoot));
    }

    private static void AssertConfigUnchanged(
        string localConfigFile,
        byte[] localBefore,
        string sharedConfigFile,
        byte[] sharedBefore)
    {
        Assert.Equal(localBefore, File.ReadAllBytes(localConfigFile));
        Assert.Equal(sharedBefore, File.ReadAllBytes(sharedConfigFile));
    }
}
