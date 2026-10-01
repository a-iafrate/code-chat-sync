using CodeChatSync.Core;

namespace CodeChatSync.Tests;

/// <summary>
/// Covers which projects the Projects page offers from the sync repository's shared
/// mapping, so the user can register one here without retyping its remote.
/// </summary>
public class AvailableProjectsTests
{
    private const string RemoteA = "https://github.com/example/client-a.git";
    private const string RemoteB = "https://github.com/example/client-b.git";

    [Fact]
    public void FindUnregistered_ReturnsASharedProjectNotRegisteredHere()
    {
        var shared = new SharedConfig();
        shared.AddOrUpdate(ProjectIdentity.FromRemote(RemoteA), "client-a");
        var local = new LocalConfig();

        var available = AvailableProjects.FindUnregistered(local, shared);

        var project = Assert.Single(available);
        Assert.Equal("github.com/example/client-a", project.Remote);
        Assert.Equal("client-a", project.Name);
    }

    [Fact]
    public void FindUnregistered_LeavesOutAProjectAlreadyRegisteredHere()
    {
        var identity = ProjectIdentity.FromRemote(RemoteA);
        var shared = new SharedConfig();
        shared.AddOrUpdate(identity, "client-a");
        var local = new LocalConfig();
        local.AddOrUpdate(identity, @"C:\work\client-a");

        Assert.Empty(AvailableProjects.FindUnregistered(local, shared));
    }

    /// <summary>
    /// A project registered here for Visual Studio only still counts as known: adding
    /// Claude Code for it goes through Claude's own add flow, not this list.
    /// </summary>
    [Fact]
    public void FindUnregistered_LeavesOutAProjectRegisteredForAnyProviderHere()
    {
        var identity = ProjectIdentity.FromRemote(RemoteA);
        var shared = new SharedConfig();
        shared.AddOrUpdate(identity, "client-a");
        var local = new LocalConfig();
        local.AddOrUpdate(identity, @"C:\work\client-a", "claudecode");

        Assert.Empty(AvailableProjects.FindUnregistered(local, shared));
    }

    [Fact]
    public void FindUnregistered_MatchesRemotesWrittenInDifferentForms()
    {
        var shared = new SharedConfig();
        shared.AddOrUpdate(ProjectIdentity.FromRemote(RemoteA), "client-a");
        var local = new LocalConfig();
        // Same project, SSH form: must still be recognized as already registered.
        local.AddOrUpdate(ProjectIdentity.FromRemote("git@github.com:example/client-a.git"), @"C:\work\client-a");

        Assert.Empty(AvailableProjects.FindUnregistered(local, shared));
    }

    [Fact]
    public void FindUnregistered_ReturnsOnlyTheProjectsMissingHere()
    {
        var shared = new SharedConfig();
        shared.AddOrUpdate(ProjectIdentity.FromRemote(RemoteA), "client-a");
        shared.AddOrUpdate(ProjectIdentity.FromRemote(RemoteB), "client-b");
        var local = new LocalConfig();
        local.AddOrUpdate(ProjectIdentity.FromRemote(RemoteA), @"C:\work\client-a");

        var available = AvailableProjects.FindUnregistered(local, shared);

        var project = Assert.Single(available);
        Assert.Equal("client-b", project.Name);
    }

    [Fact]
    public void FindUnregistered_OrdersResultsByName()
    {
        var shared = new SharedConfig();
        shared.AddOrUpdate(ProjectIdentity.FromRemote(RemoteB), "zeta");
        shared.AddOrUpdate(ProjectIdentity.FromRemote(RemoteA), "alpha");

        var available = AvailableProjects.FindUnregistered(new LocalConfig(), shared);

        Assert.Equal(["alpha", "zeta"], available.Select(project => project.Name));
    }

    /// <summary>A hand-edited or future shared config might hold something unparsable.</summary>
    [Fact]
    public void FindUnregistered_SkipsASharedEntryWithAnUnparsableRemote()
    {
        var shared = new SharedConfig();
        shared.Projects.Add(new SharedProjectEntry { Remote = string.Empty, Name = "broken" });
        shared.AddOrUpdate(ProjectIdentity.FromRemote(RemoteA), "client-a");

        var available = AvailableProjects.FindUnregistered(new LocalConfig(), shared);

        var project = Assert.Single(available);
        Assert.Equal("client-a", project.Name);
    }

    [Fact]
    public void FindUnregistered_ReturnsEmptyForAnEmptySharedConfig()
    {
        Assert.Empty(AvailableProjects.FindUnregistered(new LocalConfig(), new SharedConfig()));
    }
}
