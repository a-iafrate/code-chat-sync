namespace CodeChatSync.Git;

internal static class GitRemoteUrlGuard
{
    public static void ThrowIfContainsPassword(string remoteUrl, string parameterName)
    {
        if (Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https"
            && uri.UserInfo.Contains(':', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Remote URLs must not contain password credentials. Use a Git credential helper.",
                parameterName);
        }
    }
}
