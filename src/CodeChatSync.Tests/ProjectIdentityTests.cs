using CodeChatSync.Core;

namespace CodeChatSync.Tests;

public class ProjectIdentityTests
{
    [Theory]
    [InlineData("https://github.com/a-iafrate/code-chat-sync.git")]
    [InlineData("https://github.com/a-iafrate/code-chat-sync")]
    [InlineData("ssh://git@github.com/a-iafrate/code-chat-sync.git")]
    [InlineData("git@github.com:a-iafrate/code-chat-sync.git")]
    [InlineData("HTTPS://GitHub.com/A-Iafrate/Code-Chat-Sync.git")]
    [InlineData("  https://github.com/a-iafrate/code-chat-sync.git  ")]
    public void FromRemote_NormalizesEquivalentRemoteForms(string remote)
    {
        var identity = ProjectIdentity.FromRemote(remote);

        Assert.Equal("github.com/a-iafrate/code-chat-sync", identity.NormalizedRemote);
    }

    [Fact]
    public void FromRemote_StripsCredentialsFromHttpsRemote()
    {
        var identity = ProjectIdentity.FromRemote("https://user:token@github.com/a-iafrate/code-chat-sync.git");

        Assert.Equal("github.com/a-iafrate/code-chat-sync", identity.NormalizedRemote);
    }

    [Fact]
    public void FromRemote_DropsWwwPrefixFromHost()
    {
        var identity = ProjectIdentity.FromRemote("https://www.github.com/a-iafrate/code-chat-sync.git");

        Assert.Equal("github.com/a-iafrate/code-chat-sync", identity.NormalizedRemote);
    }

    [Fact]
    public void FromRemote_ProducesEqualIdentitiesForHttpsAndSshRemotes()
    {
        var https = ProjectIdentity.FromRemote("https://github.com/a-iafrate/code-chat-sync.git");
        var ssh = ProjectIdentity.FromRemote("git@github.com:a-iafrate/code-chat-sync.git");

        Assert.Equal(https, ssh);
    }

    [Fact]
    public void Slug_DropsHostAndReplacesSeparators()
    {
        var identity = ProjectIdentity.FromRemote("https://dev.azure.com/contoso/Internal Tools/_git/chat.sync");

        Assert.Equal("contoso-internal-tools-_git-chat.sync", identity.Slug);
    }

    [Fact]
    public void Slug_IsSafeForUseAsFolderName()
    {
        var identity = ProjectIdentity.FromRemote("git@github.com:a-iafrate/code-chat-sync.git");

        Assert.Equal("a-iafrate-code-chat-sync", identity.Slug);
        Assert.Equal(-1, identity.Slug.IndexOfAny(Path.GetInvalidFileNameChars()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void FromRemote_RejectsMissingRemote(string? remote)
    {
        Assert.ThrowsAny<ArgumentException>(() => ProjectIdentity.FromRemote(remote!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryFromRemote_ReturnsFalseForMissingRemote(string? remote)
    {
        Assert.False(ProjectIdentity.TryFromRemote(remote, out var identity));
        Assert.Null(identity);
    }

    [Fact]
    public void TryFromRemote_ReturnsIdentityForValidRemote()
    {
        Assert.True(ProjectIdentity.TryFromRemote("https://github.com/a-iafrate/code-chat-sync.git", out var identity));
        Assert.NotNull(identity);
        Assert.Equal("github.com/a-iafrate/code-chat-sync", identity!.NormalizedRemote);
    }
}
