using CodeChatSync.Git;

namespace CodeChatSync.Tests;

public sealed class GitRemoteWebUrlTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    public void TryCreate_ReturnsNullForMissingRemote(string? remoteUrl)
    {
        Assert.Null(GitRemoteWebUrl.TryCreate(remoteUrl));
    }

    [Theory]
    [InlineData("https://github.com/owner/repo", "https://github.com/owner/repo")]
    [InlineData("https://github.com/owner/repo.git", "https://github.com/owner/repo")]
    [InlineData("https://gruppostorti@dev.azure.com/gruppostorti/middleware/_git/middleware", "https://dev.azure.com/gruppostorti/middleware/_git/middleware")]
    [InlineData("https://github.com:8443/owner/repo.git", "https://github.com:8443/owner/repo")]
    [InlineData("git@github.com:owner/repo.git", "https://github.com/owner/repo")]
    [InlineData("ssh://git@host/owner/repo.git", "https://host/owner/repo")]
    [InlineData("git@ssh.dev.azure.com:v3/org/project/repo", "https://dev.azure.com/org/project/_git/repo")]
    public void TryCreate_ReturnsAbsoluteRepositoryWebUrl(string remoteUrl, string expectedAbsoluteUri)
    {
        var result = GitRemoteWebUrl.TryCreate(remoteUrl);

        Assert.Equal(expectedAbsoluteUri, result?.AbsoluteUri);
    }

    [Theory]
    [InlineData("git@ssh.dev.azure.com:v3/org/project")]
    public void TryCreate_ReturnsNullForMalformedAzureDevOpsSshPath(string remoteUrl)
    {
        Assert.Null(GitRemoteWebUrl.TryCreate(remoteUrl));
    }

    [Theory]
    [InlineData(@"C:\repos\x")]
    [InlineData("C:/repos/x")]
    [InlineData(@"\\server\share\x")]
    [InlineData("file:///C:/x")]
    [InlineData("/home/u/x")]
    public void TryCreate_ReturnsNullForLocalPaths(string remoteUrl)
    {
        Assert.Null(GitRemoteWebUrl.TryCreate(remoteUrl));
    }
}