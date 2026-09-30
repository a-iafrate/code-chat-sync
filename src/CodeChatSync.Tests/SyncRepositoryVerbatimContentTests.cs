using System.Text;
using CodeChatSync.Git;
using CodeChatSync.Tests.TestSupport;

namespace CodeChatSync.Tests;

/// <summary>
/// Chats are copied wholesale and compared by content hash, so Git must hand a file back
/// exactly as it was committed. With line-ending translation left on, a checkout alone
/// changes the hash and the next sync reports a conflict on a file nobody edited.
/// </summary>
public sealed class SyncRepositoryVerbatimContentTests
{
    private static readonly GitCommandRunner Git = new();

    [Fact]
    public void Initialize_TellsGitToStoreEveryFileUnchanged()
    {
        using var temp = new TempDirectory();
        var repository = new SyncRepository(temp.Path);

        repository.Initialize();

        Assert.Contains("* -text", File.ReadAllLines(Path.Combine(temp.Path, ".gitattributes")));
        Assert.Equal("false", ReadSetting(temp.Path, "core.autocrlf"));
    }

    [Fact]
    public void EnsureVerbatimContent_AppliesToARepositoryCreatedBeforeTheRuleExisted()
    {
        using var temp = new TempDirectory();
        Git.RunOrThrow(temp.Path, ["init"]);
        Git.RunOrThrow(temp.Path, ["config", "core.autocrlf", "true"]);

        new SyncRepository(temp.Path).EnsureVerbatimContent();

        Assert.Contains("* -text", File.ReadAllLines(Path.Combine(temp.Path, ".gitattributes")));
        Assert.Equal("false", ReadSetting(temp.Path, "core.autocrlf"));
    }

    [Fact]
    public void EnsureVerbatimContent_KeepsExistingRulesAndDoesNotRepeatItself()
    {
        using var temp = new TempDirectory();
        var repository = new SyncRepository(temp.Path);
        repository.Initialize();
        File.WriteAllText(Path.Combine(temp.Path, ".gitattributes"), "*.md text" + Environment.NewLine);

        repository.EnsureVerbatimContent();
        repository.EnsureVerbatimContent();

        var lines = File.ReadAllLines(Path.Combine(temp.Path, ".gitattributes"));
        Assert.Contains("*.md text", lines);
        Assert.Single(lines, line => line.Trim() == "* -text");
    }

    /// <summary>
    /// The behaviour that actually matters: a chat committed with one line ending must
    /// come back byte for byte, whatever the user's global Git settings say.
    /// </summary>
    [Fact]
    public void CommittedChatFileSurvivesACheckoutByteForByte()
    {
        using var temp = new TempDirectory();
        var repository = new SyncRepository(temp.Path);
        repository.Initialize();
        Git.RunOrThrow(temp.Path, ["config", "user.email", "test@example.com"]);
        Git.RunOrThrow(temp.Path, ["config", "user.name", "Test"]);

        // The setting the user is most likely to have inherited globally: the attributes
        // file has to win over it.
        Git.RunOrThrow(temp.Path, ["config", "core.autocrlf", "true"]);

        // Visual Studio writes some descriptors with Unix line endings.
        var chatPath = Path.Combine(temp.Path, "visualstudio", "demo", "session-1", "workspace.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(chatPath)!);
        var original = Encoding.UTF8.GetBytes("id: session-1\ncwd: ${project}\\\nbranch: main\n");
        File.WriteAllBytes(chatPath, original);

        Assert.True(repository.Commit("Archive a chat"));
        File.Delete(chatPath);
        Git.RunOrThrow(temp.Path, ["checkout", "--", "."]);

        Assert.Equal(original, File.ReadAllBytes(chatPath));
        Assert.Empty(repository.GetStatus().PendingChanges);
    }

    private static string ReadSetting(string repositoryPath, string name) =>
        Git.RunOrThrow(repositoryPath, ["config", "--local", "--get", name]).StandardOutput.Trim();
}
