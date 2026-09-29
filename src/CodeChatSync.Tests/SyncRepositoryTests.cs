using CodeChatSync.Git;
using CodeChatSync.Tests.TestSupport;

namespace CodeChatSync.Tests;

public sealed class SyncRepositoryTests
{
    private static readonly GitCommandRunner Git = new();

    [Fact]
    public void GetStatus_ReportsAPlainFolderAsNotARepository()
    {
        using var temp = new TempDirectory();

        var status = new SyncRepository(temp.Path).GetStatus();

        Assert.False(status.IsGitRepository);
        Assert.False(status.HasRemote);
        Assert.Empty(status.PendingChanges);
    }

    [Fact]
    public void GetStatus_ThrowsWhenTheSyncFolderIsMissing()
    {
        using var temp = new TempDirectory();
        var missing = Path.Combine(temp.Path, "gone");

        var exception = Assert.Throws<GitCommandException>(() => new SyncRepository(missing).GetStatus());

        Assert.Contains(missing, exception.Message);
    }

    [Fact]
    public void Initialize_CreatesTheRepositoryAndIgnoresBackups()
    {
        using var temp = new TempDirectory();
        var repositoryPath = Path.Combine(temp.Path, "sync");

        var repository = new SyncRepository(repositoryPath);
        repository.Initialize();

        Assert.True(repository.GetStatus().IsGitRepository);
        Assert.Contains(".backups/", File.ReadAllText(Path.Combine(repositoryPath, ".gitignore")));
    }

    [Fact]
    public void Initialize_IsSafeToRunTwiceAndKeepsExistingIgnoreRules()
    {
        using var temp = new TempDirectory();
        var repository = new SyncRepository(temp.Path);
        repository.Initialize();
        File.WriteAllText(Path.Combine(temp.Path, ".gitignore"), "*.tmp" + Environment.NewLine);

        repository.Initialize();
        repository.Initialize();

        var gitIgnore = File.ReadAllLines(Path.Combine(temp.Path, ".gitignore"));
        Assert.Contains("*.tmp", gitIgnore);
        Assert.Single(gitIgnore, line => line.Trim() == ".backups/");
    }

    [Fact]
    public void Initialize_DoesNotDuplicateTheBackupRule()
    {
        using var temp = new TempDirectory();
        var repository = new SyncRepository(temp.Path);

        repository.Initialize();
        repository.Initialize();

        var gitIgnore = File.ReadAllLines(Path.Combine(temp.Path, ".gitignore"));
        Assert.Single(gitIgnore, line => line.Trim() == ".backups/");
    }

    [Fact]
    public void Commit_ReportsWhenThereIsNothingToCommit()
    {
        using var temp = new TempDirectory();
        var repository = CreateRepository(temp.Path);
        Assert.True(repository.Commit("Initial sync"));

        var committedAgain = repository.Commit("Nothing changed");

        Assert.False(committedAgain);
    }

    [Fact]
    public void Commit_PicksUpNewChatFiles()
    {
        using var temp = new TempDirectory();
        var repository = CreateRepository(temp.Path);
        repository.Commit("Initial sync");

        Directory.CreateDirectory(Path.Combine(temp.Path, "visualstudio", "contoso-crm"));
        File.WriteAllText(Path.Combine(temp.Path, "visualstudio", "contoso-crm", "events.jsonl"), "{}");

        Assert.True(repository.Commit("Sync chats"));
        Assert.Empty(repository.GetStatus().PendingChanges);
    }

    [Fact]
    public void Commit_LeavesBackupsOutOfTheRepository()
    {
        using var temp = new TempDirectory();
        var repository = CreateRepository(temp.Path);
        repository.Commit("Initial sync");

        Directory.CreateDirectory(Path.Combine(temp.Path, ".backups", "visualstudio"));
        File.WriteAllText(Path.Combine(temp.Path, ".backups", "visualstudio", "events.jsonl"), "old");

        Assert.False(repository.Commit("Should not include backups"));
        Assert.Empty(repository.GetStatus().PendingChanges);
    }

    [Fact]
    public void GetStatus_ListsUncommittedChanges()
    {
        using var temp = new TempDirectory();
        var repository = CreateRepository(temp.Path);
        repository.Commit("Initial sync");
        File.WriteAllText(Path.Combine(temp.Path, "chat.jsonl"), "{}");

        var status = repository.GetStatus();

        Assert.True(status.HasPendingChanges);
        Assert.Contains("chat.jsonl", status.PendingChanges);
        Assert.False(status.HasRemote);
    }

    [Fact]
    public void PushThenPull_MovesChatsBetweenTwoPcs()
    {
        using var temp = new TempDirectory();
        var remotePath = Path.Combine(temp.Path, "remote.git");
        Git.RunOrThrow(temp.Path, ["init", "--bare", remotePath]);

        var firstPc = CreateRepository(Path.Combine(temp.Path, "pc-one"), remotePath);
        File.WriteAllText(Path.Combine(firstPc.RepositoryPath, "chat.jsonl"), "from pc-one");
        Assert.True(firstPc.Commit("Sync chats from pc-one"));
        var push = firstPc.Push();
        Assert.True(push.Succeeded, push.ErrorMessage);

        Git.RunOrThrow(temp.Path, ["clone", remotePath, Path.Combine(temp.Path, "pc-two")]);
        var secondPc = new SyncRepository(Path.Combine(temp.Path, "pc-two"));
        Assert.Equal("from pc-one", File.ReadAllText(Path.Combine(secondPc.RepositoryPath, "chat.jsonl")));

        File.WriteAllText(Path.Combine(firstPc.RepositoryPath, "chat.jsonl"), "updated on pc-one");
        firstPc.Commit("Update chats");
        Assert.True(firstPc.Push().Succeeded);

        ConfigureIdentity(secondPc.RepositoryPath);
        var pull = secondPc.Pull();

        Assert.Equal(PullStatus.UpToDate, pull.Status);
        Assert.Equal("updated on pc-one", File.ReadAllText(Path.Combine(secondPc.RepositoryPath, "chat.jsonl")));
        Assert.True(secondPc.GetStatus().HasRemote);
    }

    [Fact]
    public void Pull_FailsOnDivergenceInsteadOfMergingTranscripts()
    {
        using var temp = new TempDirectory();
        var remotePath = Path.Combine(temp.Path, "remote.git");
        Git.RunOrThrow(temp.Path, ["init", "--bare", remotePath]);

        var firstPc = CreateRepository(Path.Combine(temp.Path, "pc-one"), remotePath);
        File.WriteAllText(Path.Combine(firstPc.RepositoryPath, "chat.jsonl"), "shared");
        firstPc.Commit("Initial sync");
        Assert.True(firstPc.Push().Succeeded);

        Git.RunOrThrow(temp.Path, ["clone", remotePath, Path.Combine(temp.Path, "pc-two")]);
        var secondPc = new SyncRepository(Path.Combine(temp.Path, "pc-two"));
        ConfigureIdentity(secondPc.RepositoryPath);

        File.WriteAllText(Path.Combine(firstPc.RepositoryPath, "chat.jsonl"), "changed on pc-one");
        firstPc.Commit("Change on pc-one");
        Assert.True(firstPc.Push().Succeeded);

        File.WriteAllText(Path.Combine(secondPc.RepositoryPath, "chat.jsonl"), "changed on pc-two");
        secondPc.Commit("Change on pc-two");

        var pull = secondPc.Pull();

        Assert.Equal(PullStatus.Failed, pull.Status);
        Assert.False(pull.CanContinue);
        Assert.Equal("changed on pc-two", File.ReadAllText(Path.Combine(secondPc.RepositoryPath, "chat.jsonl")));
    }

    [Fact]
    public void Pull_IsNotAFailureWhenThereIsNoRemoteYet()
    {
        using var temp = new TempDirectory();
        var repository = CreateRepository(temp.Path);

        var pull = repository.Pull();

        Assert.Equal(PullStatus.NoRemote, pull.Status);
        Assert.True(pull.CanContinue);
    }

    [Fact]
    public void Pull_IsNotAFailureBeforeTheFirstPush()
    {
        using var temp = new TempDirectory();
        var remotePath = Path.Combine(temp.Path, "remote.git");
        Git.RunOrThrow(temp.Path, ["init", "--bare", remotePath]);
        var repository = CreateRepository(Path.Combine(temp.Path, "pc-one"), remotePath);
        repository.Commit("Initial sync");

        var pull = repository.Pull();

        Assert.Equal(PullStatus.NoUpstream, pull.Status);
        Assert.True(pull.CanContinue);
    }

    [Fact]
    public void GetAndSetOriginRemoteUrl_InitializesAddsAndReplacesOrigin()
    {
        using var temp = new TempDirectory();
        var repository = new SyncRepository(temp.Combine("sync"));
        repository.Initialize();

        Assert.Null(repository.GetOriginRemoteUrl());

        const string initialUrl = "https://github.com/contoso/chat-sync.git";
        const string updatedUrl = "https://github.com/fabrikam/chat-sync.git";
        repository.SetOriginRemoteUrl(initialUrl);
        Assert.Equal(initialUrl, repository.GetOriginRemoteUrl());

        repository.SetOriginRemoteUrl(updatedUrl);

        Assert.Equal(updatedUrl, repository.GetOriginRemoteUrl());
        Assert.True(repository.GetStatus().HasRemote);
    }

    [Theory]
    [InlineData("http://user:password@example.com/team/sync.git")]
    [InlineData("https://user:password@example.com/team/sync.git")]
    public void SetOriginRemoteUrl_RejectsEmbeddedCredentialsWithoutReplacingValidOrigin(string invalidUrl)
    {
        using var temp = new TempDirectory();
        var repository = new SyncRepository(temp.Path);
        repository.Initialize();
        const string validUrl = "https://github.com/contoso/sync.git";
        repository.SetOriginRemoteUrl(validUrl);

        var exception = Assert.Throws<ArgumentException>(() => repository.SetOriginRemoteUrl(invalidUrl));

        Assert.Contains("credentials", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(validUrl, repository.GetOriginRemoteUrl());
        Assert.True(repository.GetStatus().HasRemote);
    }

    private static SyncRepository CreateRepository(string path, string? remotePath = null)
    {
        var repository = new SyncRepository(path);
        repository.Initialize();
        ConfigureIdentity(repository.RepositoryPath);

        if (remotePath is not null)
        {
            Git.RunOrThrow(repository.RepositoryPath, ["remote", "add", "origin", remotePath]);
        }

        return repository;
    }

    /// <summary>
    /// Commits need an author, and the machine running the tests may have none configured.
    /// </summary>
    private static void ConfigureIdentity(string repositoryPath)
    {
        Git.RunOrThrow(repositoryPath, ["config", "user.name", "CodeChatSync Tests"]);
        Git.RunOrThrow(repositoryPath, ["config", "user.email", "tests@codechatsync.invalid"]);
    }
}
