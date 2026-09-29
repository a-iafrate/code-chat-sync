using CodeChatSync.Git;
using CodeChatSync.Tests.TestSupport;

namespace CodeChatSync.Tests;

public class GitRemoteReaderTests : IDisposable
{
    private readonly TempDirectory _root = new();

    public void Dispose() => _root.Dispose();

    [Fact]
    public void FindRepositoryRoot_WalksUpFromASubfolder()
    {
        var repository = CreateRepository("[remote \"origin\"]\n\turl = https://github.com/a-iafrate/code-chat-sync.git\n");
        var nested = Path.Combine(repository, "src", "Project");
        Directory.CreateDirectory(nested);

        Assert.Equal(repository, GitRemoteReader.FindRepositoryRoot(nested));
    }

    [Fact]
    public void FindRepositoryRoot_ReturnsNullOutsideARepository()
    {
        var plain = _root.Combine("plain");
        Directory.CreateDirectory(plain);

        Assert.Null(GitRemoteReader.FindRepositoryRoot(plain));
    }

    [Fact]
    public void FindPrimaryRemoteUrl_ReadsOrigin()
    {
        var repository = CreateRepository(
            "[core]\n\tbare = false\n[remote \"origin\"]\n\turl = https://github.com/a-iafrate/code-chat-sync.git\n\tfetch = +refs/heads/*:refs/remotes/origin/*\n");

        Assert.Equal("https://github.com/a-iafrate/code-chat-sync.git", GitRemoteReader.FindPrimaryRemoteUrl(repository));
    }

    [Fact]
    public void FindPrimaryRemoteUrl_PrefersOriginOverOtherRemotes()
    {
        var repository = CreateRepository(
            "[remote \"upstream\"]\n\turl = https://github.com/contoso/upstream.git\n[remote \"origin\"]\n\turl = https://github.com/a-iafrate/code-chat-sync.git\n");

        Assert.Equal("https://github.com/a-iafrate/code-chat-sync.git", GitRemoteReader.FindPrimaryRemoteUrl(repository));
    }

    [Fact]
    public void FindPrimaryRemoteUrl_UsesTheOnlyRemoteWhenOriginIsAbsent()
    {
        var repository = CreateRepository("[remote \"upstream\"]\n\turl = git@github.com:contoso/upstream.git\n");

        Assert.Equal("git@github.com:contoso/upstream.git", GitRemoteReader.FindPrimaryRemoteUrl(repository));
    }

    [Fact]
    public void FindPrimaryRemoteUrl_ReturnsNullWhenTheChoiceWouldBeAmbiguous()
    {
        var repository = CreateRepository(
            "[remote \"first\"]\n\turl = https://github.com/contoso/first.git\n[remote \"second\"]\n\turl = https://github.com/contoso/second.git\n");

        Assert.Null(GitRemoteReader.FindPrimaryRemoteUrl(repository));
    }

    [Fact]
    public void FindPrimaryRemoteUrl_ReturnsNullWhenNoRemoteIsConfigured()
    {
        var repository = CreateRepository("[core]\n\tbare = false\n");

        Assert.Null(GitRemoteReader.FindPrimaryRemoteUrl(repository));
    }

    [Fact]
    public void ReadRemotes_IgnoresCommentsAndNonRemoteSections()
    {
        var repository = CreateRepository(
            "# a comment\n[branch \"main\"]\n\turl = not-a-remote\n[remote \"origin\"]\n\t; inline comment\n\turl = https://github.com/a-iafrate/code-chat-sync.git\n");

        var remotes = GitRemoteReader.ReadRemotes(repository);

        var remote = Assert.Single(remotes);
        Assert.Equal("origin", remote.Name);
        Assert.Equal("https://github.com/a-iafrate/code-chat-sync.git", remote.Url);
    }

    [Fact]
    public void ReadRemotes_FollowsTheGitdirPointerUsedByWorktrees()
    {
        var actualGitDirectory = _root.Combine("shared.git");
        Directory.CreateDirectory(actualGitDirectory);
        File.WriteAllText(
            Path.Combine(actualGitDirectory, "config"),
            "[remote \"origin\"]\n\turl = https://github.com/a-iafrate/code-chat-sync.git\n");

        var worktree = _root.Combine("worktree");
        Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {actualGitDirectory}");

        var remote = Assert.Single(GitRemoteReader.ReadRemotes(worktree));
        Assert.Equal("https://github.com/a-iafrate/code-chat-sync.git", remote.Url);
    }

    [Fact]
    public void ReadRemotes_ReturnsEmptyOutsideARepository()
    {
        var plain = _root.Combine("plain");
        Directory.CreateDirectory(plain);

        Assert.Empty(GitRemoteReader.ReadRemotes(plain));
    }

    private string CreateRepository(string configContent)
    {
        var repository = _root.Combine("repo");
        var gitDirectory = Path.Combine(repository, ".git");
        Directory.CreateDirectory(gitDirectory);
        File.WriteAllText(Path.Combine(gitDirectory, "config"), configContent);
        return repository;
    }
}
