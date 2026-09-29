using System.Text;

namespace CodeChatSync.Core;

/// <summary>
/// Identifies a project by its Git remote rather than by its local path, so the
/// same project is recognized across PCs where it lives in different folders.
/// </summary>
public sealed record ProjectIdentity
{
    private ProjectIdentity(string normalizedRemote, string slug)
    {
        NormalizedRemote = normalizedRemote;
        Slug = slug;
    }

    /// <summary>Remote reduced to <c>host/owner/name</c>, without scheme, credentials or <c>.git</c>.</summary>
    public string NormalizedRemote { get; }

    /// <summary>Filesystem-safe name derived from the remote, used as the sync folder name.</summary>
    public string Slug { get; }

    /// <summary>
    /// Normalizes a Git remote URL. Supports HTTPS, SSH and <c>scp</c>-style remotes,
    /// which all denote the same project.
    /// </summary>
    public static ProjectIdentity FromRemote(string remote)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remote);

        var normalized = NormalizeRemote(remote);
        if (normalized.Length == 0)
        {
            throw new FormatException($"Unable to derive a project identity from remote '{remote}'.");
        }

        return new ProjectIdentity(normalized, CreateSlug(normalized));
    }

    /// <summary>Normalizes a remote, returning <see langword="false"/> instead of throwing.</summary>
    public static bool TryFromRemote(string? remote, out ProjectIdentity? identity)
    {
        identity = null;
        if (string.IsNullOrWhiteSpace(remote))
        {
            return false;
        }

        try
        {
            identity = FromRemote(remote);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string NormalizeRemote(string remote)
    {
        var value = remote.Trim();

        // scp-style remotes such as git@github.com:owner/repo.git
        if (!value.Contains("://", StringComparison.Ordinal))
        {
            var colonIndex = value.IndexOf(':');
            if (colonIndex > 0)
            {
                var host = value[..colonIndex];
                var path = value[(colonIndex + 1)..];
                value = $"ssh://{host}/{path}";
            }
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && !uri.IsFile)
        {
            var host = uri.Host;
            // Remotes may percent-encode spaces and other characters; decode so the
            // same project yields the same identity however the URL was written.
            var path = Uri.UnescapeDataString(uri.AbsolutePath);
            return Combine(host, path);
        }

        // Fall back to treating the value as a bare path, e.g. a local sync repo.
        return Combine(host: string.Empty, path: value.Replace('\\', '/'));
    }

    private static string Combine(string host, string path)
    {
        var trimmedPath = path.Trim('/');
        if (trimmedPath.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            trimmedPath = trimmedPath[..^4];
        }

        trimmedPath = trimmedPath.Trim('/');

        var normalizedHost = host.Trim('/').ToLowerInvariant();
        if (normalizedHost.StartsWith("www.", StringComparison.Ordinal))
        {
            normalizedHost = normalizedHost[4..];
        }

        return normalizedHost.Length == 0
            ? trimmedPath.ToLowerInvariant()
            : $"{normalizedHost}/{trimmedPath.ToLowerInvariant()}";
    }

    private static string CreateSlug(string normalizedRemote)
    {
        // Drop the host so the folder name stays short and readable.
        var withoutHost = normalizedRemote.Contains('/')
            ? normalizedRemote[(normalizedRemote.IndexOf('/') + 1)..]
            : normalizedRemote;

        var builder = new StringBuilder(withoutHost.Length);
        var lastWasSeparator = false;
        foreach (var character in withoutHost)
        {
            if (char.IsLetterOrDigit(character) || character is '.' or '_')
            {
                builder.Append(character);
                lastWasSeparator = false;
            }
            else if (!lastWasSeparator)
            {
                builder.Append('-');
                lastWasSeparator = true;
            }
        }

        var slug = builder.ToString().Trim('-', '.');
        return slug.Length == 0 ? "project" : slug;
    }

    public override string ToString() => NormalizedRemote;
}
