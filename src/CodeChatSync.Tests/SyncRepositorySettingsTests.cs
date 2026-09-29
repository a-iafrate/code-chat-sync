using CodeChatSync.Core;
using CodeChatSync.Git;
using CodeChatSync.Tests.TestSupport;

namespace CodeChatSync.Tests;

public sealed class SyncRepositorySettingsTests : IDisposable
{
    private const string ProjectRemote = "https://github.com/contoso/client.git";
    private static readonly GitCommandRunner Git = new();

    private readonly TempDirectory _root = new();

    public void Dispose() => _root.Dispose();

    [Fact]
    public void Save_InitializesSyncRootAndPersistsOriginUpdatesAndOtherSettings()
    {
        var configPath = _root.Combine("local-config.json");
        var projectPath = _root.Combine("client-project");
        var config = new LocalConfig();
        config.AddOrUpdate(ProjectIdentity.FromRemote(ProjectRemote), projectPath);
        config.Save(configPath);
        var syncRoot = _root.Combine("sync-root");
        const string initialUrl = "https://github.com/contoso/shared-sync.git";
        const string updatedUrl = "https://github.com/fabrikam/shared-sync.git";

        SyncRepositorySettings.Save(syncRoot, initialUrl, initialize: true, configPath);

        var repository = new SyncRepository(syncRoot);
        Assert.True(repository.GetStatus().IsGitRepository);
        Assert.Equal(initialUrl, repository.GetOriginRemoteUrl());
        Assert.Equal(Path.GetFullPath(syncRoot), LocalConfig.Load(configPath).SyncRootPath);

        SyncRepositorySettings.Save(syncRoot, updatedUrl, initialize: false, configPath);

        var reloaded = LocalConfig.Load(configPath);
        Assert.Equal(updatedUrl, repository.GetOriginRemoteUrl());
        Assert.True(repository.GetStatus().HasRemote);
        Assert.Equal(Path.GetFullPath(syncRoot), reloaded.SyncRootPath);
        var savedProject = Assert.Single(reloaded.Projects);
        Assert.Equal("github.com/contoso/client", savedProject.Remote);
        Assert.Equal(Path.GetFullPath(projectPath), savedProject.LocalPath);
    }

    [Fact]
    public void Save_RejectsSyncRootNestedInInitializedClientRepositoryWithoutSavingIt()
    {
        var clientRepository = _root.Combine("client-repository");
        Directory.CreateDirectory(clientRepository);
        Git.RunOrThrow(clientRepository, ["init"]);
        var nestedSyncRoot = Directory.CreateDirectory(Path.Combine(clientRepository, "sync-root")).FullName;
        var configPath = _root.Combine("local-config.json");

        var exception = Assert.Throws<ArgumentException>(() =>
            SyncRepositorySettings.Save(nestedSyncRoot, null, initialize: true, configPath));

        Assert.Contains("inside another Git repository", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(configPath));
        Assert.Null(LocalConfig.Load(configPath).SyncRootPath);
        Assert.True(GitRemoteReader.FindRepositoryRoot(nestedSyncRoot) == clientRepository);
    }

    [Fact]
    public void Save_RejectsSyncRootContainingRegisteredClientProjectWithoutChangingConfiguration()
    {
        var configPath = _root.Combine("local-config.json");
        var previousSyncRoot = _root.Combine("previous-sync-root");
        var syncRoot = Directory.CreateDirectory(_root.Combine("sync-root")).FullName;
        var clientProject = Directory.CreateDirectory(Path.Combine(syncRoot, "client-project")).FullName;
        var config = new LocalConfig { SyncRootPath = previousSyncRoot };
        config.AddOrUpdate(ProjectIdentity.FromRemote(ProjectRemote), clientProject);
        config.Save(configPath);
        var previousBytes = File.ReadAllBytes(configPath);

        var exception = Assert.Throws<ArgumentException>(() =>
            SyncRepositorySettings.Save(syncRoot, null, initialize: true, configPath));

        Assert.Contains("overlap a registered client project", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(syncRoot, ".git")));
        AssertConfigUnchanged(configPath, previousBytes, previousSyncRoot, clientProject);
    }
    [Fact]
    public void Save_RejectsCredentialRemoteAndPreservesExistingConfiguration()
    {
        var configPath = _root.Combine("local-config.json");
        var existing = SeedExistingConfig(configPath);
        var previousBytes = File.ReadAllBytes(configPath);
        var targetSyncRoot = _root.Combine("failed-remote-sync");

        var exception = Assert.Throws<ArgumentException>(() => SyncRepositorySettings.Save(
            targetSyncRoot,
            "https://user:password@example.com/team/sync.git",
            initialize: true,
            configPath));

        Assert.Contains("credentials", exception.Message, StringComparison.OrdinalIgnoreCase);
        var failedRepository = new SyncRepository(targetSyncRoot);
        Assert.True(failedRepository.GetStatus().IsGitRepository);
        Assert.Null(failedRepository.GetOriginRemoteUrl());
        AssertConfigUnchanged(configPath, previousBytes, existing.SyncRootPath, existing.ProjectPath);
    }

    [Fact]
    public void Save_RejectsOriginForNonInitializedFolderAndPreservesExistingConfiguration()
    {
        var configPath = _root.Combine("local-config.json");
        var existing = SeedExistingConfig(configPath);
        var previousBytes = File.ReadAllBytes(configPath);
        var invalidSyncRoot = _root.Combine("plain-folder");
        Directory.CreateDirectory(invalidSyncRoot);
        var markerPath = Path.Combine(invalidSyncRoot, "keep.txt");
        File.WriteAllText(markerPath, "not a Git repository");

        var exception = Assert.Throws<GitCommandException>(() => SyncRepositorySettings.Save(
            invalidSyncRoot,
            "https://github.com/contoso/shared-sync.git",
            initialize: false,
            configPath));

        Assert.Contains("root of its own Git repository", exception.Message, StringComparison.Ordinal);
        Assert.Equal("not a Git repository", File.ReadAllText(markerPath));
        AssertConfigUnchanged(configPath, previousBytes, existing.SyncRootPath, existing.ProjectPath);
    }

    private (string SyncRootPath, string ProjectPath) SeedExistingConfig(string configPath)
    {
        var syncRootPath = _root.Combine("previous-sync-root");
        var projectPath = _root.Combine("registered-client");
        var config = new LocalConfig { SyncRootPath = syncRootPath };
        config.AddOrUpdate(ProjectIdentity.FromRemote(ProjectRemote), projectPath);
        config.Save(configPath);
        return (syncRootPath, projectPath);
    }

    private static void AssertConfigUnchanged(
        string configPath,
        byte[] expectedBytes,
        string expectedSyncRootPath,
        string expectedProjectPath)
    {
        Assert.Equal(expectedBytes, File.ReadAllBytes(configPath));
        var reloaded = LocalConfig.Load(configPath);
        Assert.Equal(expectedSyncRootPath, reloaded.SyncRootPath);
        var project = Assert.Single(reloaded.Projects);
        Assert.Equal("github.com/contoso/client", project.Remote);
        Assert.Equal(expectedProjectPath, project.LocalPath);
    }
}
