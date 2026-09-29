using CodeChatSync.Core;
using CodeChatSync.Git;
using CodeChatSync.Tests.TestSupport;

namespace CodeChatSync.Tests;

/// <summary>
/// Registering more providers for a project already known on this PC. Uses explicit
/// remotes on synthetic folders, so no Git repository, network or real chat data is needed.
/// </summary>
public sealed class ProjectRegistrationMultiProviderTests : IDisposable
{
    private const string HttpsRemote = "https://github.com/acme/portal.git";
    private const string SshRemote = "git@github.com:Acme/Portal.git";

    private readonly TempDirectory _root = new();

    private string ConfigPath => _root.Combine("pc", "local-config.json");

    public void Dispose() => _root.Dispose();

    [Fact]
    public void Add_ClaudeForSameNormalizedRemote_PreservesVisualStudioAndSharedName()
    {
        var syncRoot = CreateDirectory("sync");
        var projectPath = CreateDirectory("portal");
        new LocalConfig { SyncRootPath = syncRoot }.Save(ConfigPath);

        var visualStudio = ProjectRegistration.Add(projectPath, "client-portal", HttpsRemote, ConfigPath);
        var claude = ProjectRegistration.Add(projectPath, remoteOverride: SshRemote, configPath: ConfigPath, providerId: "claudecode");

        Assert.Equal(visualStudio.Identity, claude.Identity);
        Assert.Equal("client-portal", claude.DisplayName);
        var entry = Assert.Single(LocalConfig.Load(ConfigPath).Projects);
        Assert.Equal("github.com/acme/portal", entry.Remote);
        Assert.Equal(Path.GetFullPath(projectPath), entry.LocalPath);
        Assert.Equal(["visualstudio", "claudecode"], entry.ProviderIds);
        var shared = Assert.Single(SharedConfig.Load(syncRoot).Projects);
        Assert.Equal("github.com/acme/portal", shared.Remote);
        Assert.Equal("client-portal", shared.Name);
    }

    [Fact]
    public void Add_ClaudeToLegacyEntryWithoutProviderIds_KeepsImplicitVisualStudio()
    {
        var syncRoot = CreateDirectory("sync");
        var projectPath = CreateDirectory("portal");
        new LocalConfig
        {
            SyncRootPath = syncRoot,
            Projects = [new LocalProjectEntry { Remote = "github.com/acme/portal", LocalPath = projectPath }]
        }.Save(ConfigPath);
        Assert.DoesNotContain("providerIds", File.ReadAllText(ConfigPath));

        ProjectRegistration.Add(projectPath, remoteOverride: SshRemote, configPath: ConfigPath, providerId: "ClaudeCode");

        var entry = Assert.Single(LocalConfig.Load(ConfigPath).Projects);
        Assert.Equal(["visualstudio", "claudecode"], entry.ProviderIds);
    }

    [Fact]
    public void Add_SameProviderTwice_DoesNotDuplicateProviderIds()
    {
        var syncRoot = CreateDirectory("sync");
        var projectPath = CreateDirectory("portal");
        new LocalConfig { SyncRootPath = syncRoot }.Save(ConfigPath);

        ProjectRegistration.Add(projectPath, remoteOverride: HttpsRemote, configPath: ConfigPath, providerId: "claudecode");
        ProjectRegistration.Add(projectPath, remoteOverride: SshRemote, configPath: ConfigPath, providerId: "claudecode");

        var entry = Assert.Single(LocalConfig.Load(ConfigPath).Projects);
        Assert.Equal(["claudecode"], entry.ProviderIds);
    }

    [Fact]
    public void Add_ClaudeRenameWhenClaudeArchiveExists_IsRejectedWithoutChangingConfiguration()
    {
        var syncRoot = CreateDirectory("sync");
        var projectPath = CreateDirectory("portal");
        new LocalConfig { SyncRootPath = syncRoot }.Save(ConfigPath);
        ProjectRegistration.Add(projectPath, "client-portal", HttpsRemote, ConfigPath, "claudecode");
        _root.WriteFile("sync/claudecode/client-portal/session/events.jsonl", "archived");
        var localBefore = File.ReadAllBytes(ConfigPath);
        var sharedBefore = File.ReadAllBytes(SharedConfig.GetPath(syncRoot));

        Assert.Throws<InvalidOperationException>(() =>
            ProjectRegistration.Add(projectPath, "renamed", SshRemote, ConfigPath, "visualstudio"));

        Assert.Equal(localBefore, File.ReadAllBytes(ConfigPath));
        Assert.Equal(sharedBefore, File.ReadAllBytes(SharedConfig.GetPath(syncRoot)));
    }

    private string CreateDirectory(string name) => Directory.CreateDirectory(_root.Combine(name)).FullName;
}