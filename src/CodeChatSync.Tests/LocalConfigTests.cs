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
}
