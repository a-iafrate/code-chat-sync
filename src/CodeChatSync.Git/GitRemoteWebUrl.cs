namespace CodeChatSync.Git;

/// <summary>Converts a Git remote URL into the web page of the repository, when one can be inferred.</summary>
public static class GitRemoteWebUrl
{
    private const string AzureDevOpsSshHost = "ssh.dev.azure.com";

    /// <summary>
    /// Returns an https URL for the repository page, or null when the remote is local or not recognized.
    /// Credentials are never included in the result.
    /// </summary>
    public static Uri? TryCreate(string? remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl))
        {
            return null;
        }

        var trimmed = remoteUrl.Trim();
        string host;
        string path;
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "ssh" or "git")
        {
            host = uri.Host;
            path = uri.AbsolutePath;
            if (uri.Scheme is "http" or "https")
            {
                return Build(uri.Scheme, host, uri.IsDefaultPort ? null : uri.Port, path);
            }
        }
        else if (!TryParseScpLike(trimmed, out host, out path))
        {
            return null;
        }

        path = path.TrimStart('/');
        if (string.Equals(host, AzureDevOpsSshHost, StringComparison.OrdinalIgnoreCase))
        {
            // ssh.dev.azure.com:v3/{organization}/{project}/{repository}
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length != 4 || !string.Equals(segments[0], "v3", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return Build("https", "dev.azure.com", null, $"{segments[1]}/{segments[2]}/_git/{segments[3]}");
        }

        return Build("https", host, null, path);
    }

    private static bool TryParseScpLike(string remote, out string host, out string path)
    {
        host = string.Empty;
        path = string.Empty;
        var colon = remote.IndexOf(':');
        if (colon <= 0 || remote.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        var hostPart = remote[..colon];
        var at = hostPart.LastIndexOf('@');
        host = at >= 0 ? hostPart[(at + 1)..] : hostPart;

        // A single letter before the colon is a Windows drive, not a host.
        if (host.Length <= 1 || host.Contains('\\') || host.Contains('/'))
        {
            return false;
        }

        path = remote[(colon + 1)..];
        return path.Length > 0;
    }

    private static Uri? Build(string scheme, string host, int? port, string path)
    {
        if (host.Length == 0)
        {
            return null;
        }

        path = path.Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^4];
        }

        if (path.Length == 0)
        {
            return null;
        }

        var builder = new UriBuilder(scheme, host) { Path = path };
        if (port is { } explicitPort)
        {
            builder.Port = explicitPort;
        }
        else
        {
            builder.Port = -1;
        }

        return builder.Uri;
    }
}
