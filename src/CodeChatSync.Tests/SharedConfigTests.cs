using CodeChatSync.Core;
using CodeChatSync.Tests.TestSupport;

namespace CodeChatSync.Tests;

public class SharedConfigTests : IDisposable
{
    private const string Remote = "https://github.com/a-iafrate/code-chat-sync.git";

    private readonly TempDirectory _root = new();

    public void Dispose() => _root.Dispose();

    [Fact]
    public void Load_ReturnsEmptyConfigWhenTheSyncRepositoryHasNoneYet()
    {
        var config = SharedConfig.Load(_root.Path);

        Assert.Empty(config.Projects);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsTheMapping()
    {
        var config = new SharedConfig();
        config.AddOrUpdate(ProjectIdentity.FromRemote(Remote), "client-erp");
        config.Save(_root.Path);

        var reloaded = SharedConfig.Load(_root.Path);

        var entry = Assert.Single(reloaded.Projects);
        Assert.Equal("github.com/a-iafrate/code-chat-sync", entry.Remote);
        Assert.Equal("client-erp", entry.Name);
    }

    [Fact]
    public void SharedConfig_ContainsNoLocalPaths()
    {
        var config = new SharedConfig();
        config.AddOrUpdate(ProjectIdentity.FromRemote(Remote), "client-erp");
        config.Save(_root.Path);

        var json = File.ReadAllText(SharedConfig.GetPath(_root.Path));

        Assert.DoesNotContain(_root.Path, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("localPath", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AddOrUpdate_DefaultsToTheIdentitySlug()
    {
        var config = new SharedConfig();

        var entry = config.AddOrUpdate(ProjectIdentity.FromRemote(Remote));

        Assert.Equal("a-iafrate-code-chat-sync", entry.Name);
    }

    [Fact]
    public void AddOrUpdate_KeepsTheExistingNameWhenNoneIsGiven()
    {
        var identity = ProjectIdentity.FromRemote(Remote);
        var config = new SharedConfig();
        config.AddOrUpdate(identity, "client-erp");

        var entry = config.AddOrUpdate(identity);

        Assert.Equal("client-erp", entry.Name);
        Assert.Single(config.Projects);
    }

    [Fact]
    public void AddOrUpdate_RenamesOnlyWhenANewNameIsGiven()
    {
        var identity = ProjectIdentity.FromRemote(Remote);
        var config = new SharedConfig();
        config.AddOrUpdate(identity, "client-erp");

        var entry = config.AddOrUpdate(identity, "client-erp-v2");

        Assert.Equal("client-erp-v2", entry.Name);
        Assert.Single(config.Projects);
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("../escaped")]
    [InlineData("nested/name")]
    [InlineData("nested\\name")]
    [InlineData("bad:name")]
    public void AddOrUpdate_RejectsNamesThatAreNotASingleFolder(string name)
    {
        var config = new SharedConfig();

        Assert.Throws<ArgumentException>(() => config.AddOrUpdate(ProjectIdentity.FromRemote(Remote), name));
    }

    [Fact]
    public void Load_ReportsCorruptConfigurationInsteadOfSilentlyDiscardingIt()
    {
        File.WriteAllText(SharedConfig.GetPath(_root.Path), "{ not json");

        Assert.Throws<InvalidDataException>(() => SharedConfig.Load(_root.Path));
    }
}
