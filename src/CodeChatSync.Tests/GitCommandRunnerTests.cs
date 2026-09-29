using CodeChatSync.Git;
using CodeChatSync.Tests.TestSupport;

namespace CodeChatSync.Tests;

public sealed class GitCommandRunnerTests
{
    [Fact]
    public void CheckAvailability_ReportsTheInstalledVersion()
    {
        var availability = new GitCommandRunner().CheckAvailability();

        Assert.True(availability.IsAvailable, availability.Description);
        Assert.NotNull(availability.Version);
        Assert.StartsWith("git version", availability.Version);
        Assert.Null(availability.Problem);
    }

    [Fact]
    public void CheckAvailability_ReportsAProblemWhenGitIsMissing()
    {
        var runner = new GitCommandRunner("codechatsync-not-a-real-git");

        var availability = runner.CheckAvailability();

        Assert.False(availability.IsAvailable);
        Assert.Null(availability.Version);
        Assert.NotNull(availability.Problem);
        Assert.Contains("codechatsync-not-a-real-git", availability.Description);
        Assert.Equal(availability.Problem, availability.Description);
    }

    [Fact]
    public void EnsureAvailable_ThrowsWhenGitIsMissing()
    {
        var runner = new GitCommandRunner("codechatsync-not-a-real-git");

        var exception = Assert.Throws<GitCommandException>(runner.EnsureAvailable);

        Assert.Contains("PATH", exception.Message);
    }

    [Fact]
    public void Run_ThrowsWhenTheWorkingDirectoryIsMissing()
    {
        using var temp = new TempDirectory();
        var missing = Path.Combine(temp.Path, "gone");

        var exception = Assert.Throws<GitCommandException>(
            () => new GitCommandRunner().Run(missing, ["status"]));

        Assert.Contains(missing, exception.Message);
    }

    [Fact]
    public void Run_ReportsFailureWithoutThrowing()
    {
        using var temp = new TempDirectory();

        var result = new GitCommandRunner().Run(temp.Path, ["rev-parse", "--is-inside-work-tree"]);

        Assert.False(result.Succeeded);
        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public void RunOrThrow_ThrowsOnFailure()
    {
        using var temp = new TempDirectory();

        var exception = Assert.Throws<GitCommandException>(
            () => new GitCommandRunner().RunOrThrow(temp.Path, ["rev-parse", "--is-inside-work-tree"]));

        Assert.Contains("rev-parse", exception.Message);
    }
}
