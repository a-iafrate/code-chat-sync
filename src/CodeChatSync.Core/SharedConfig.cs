using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeChatSync.Core;

/// <summary>Remote-to-name mapping for one project, shared across the user's PCs.</summary>
public sealed record SharedProjectEntry
{
    /// <summary>Normalized Git remote identifying the project.</summary>
    [JsonPropertyName("remote")]
    public required string Remote { get; init; }

    /// <summary>Folder name used for the project inside the sync repository.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }
}

/// <summary>
/// Configuration stored in the sync repository and versioned with it: the mapping
/// from Git remote to project folder name. It holds no local paths, so it stays
/// valid on every PC.
/// </summary>
public sealed class SharedConfig
{
    /// <summary>File name of this configuration inside the sync repository.</summary>
    public const string FileName = "codechatsync.json";

    [JsonPropertyName("projects")]
    public List<SharedProjectEntry> Projects { get; set; } = [];

    public static string GetPath(string syncRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(syncRootPath);
        return Path.Combine(Path.GetFullPath(syncRootPath), FileName);
    }

    public static SharedConfig Load(string syncRootPath)
    {
        var path = GetPath(syncRootPath);
        if (!File.Exists(path))
        {
            return new SharedConfig();
        }

        try
        {
            return JsonSerializer.Deserialize<SharedConfig>(File.ReadAllText(path), ConfigJson.Options)
                ?? new SharedConfig();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Shared configuration at '{path}' is not valid JSON.", exception);
        }
    }

    public void Save(string syncRootPath)
    {
        var path = GetPath(syncRootPath);
        var directory = Path.GetDirectoryName(path);
        if (directory is { Length: > 0 })
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(this, ConfigJson.Options));
    }

    public SharedProjectEntry? Find(ProjectIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        return Projects.FirstOrDefault(entry =>
            ProjectIdentity.TryFromRemote(entry.Remote, out var entryIdentity)
            && entryIdentity is not null
            && string.Equals(
                entryIdentity.NormalizedRemote,
                identity.NormalizedRemote,
                StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Registers a project under <paramref name="name"/>, defaulting to the identity
    /// slug. An existing name is kept unless a new one is given, so a folder already
    /// present in the sync repository is not renamed by accident.
    /// </summary>
    public SharedProjectEntry AddOrUpdate(ProjectIdentity identity, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var existing = Find(identity);
        var resolvedName = SanitizeName(name) ?? existing?.Name ?? identity.Slug;
        var entry = new SharedProjectEntry { Remote = identity.NormalizedRemote, Name = resolvedName };

        if (existing is not null)
        {
            Projects.Remove(existing);
        }

        Projects.Add(entry);
        return entry;
    }

    /// <summary>
    /// Rejects names that are not usable as a single folder, so a configured name can
    /// never redirect writes outside the project's folder.
    /// </summary>
    private static string? SanitizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var trimmed = name.Trim();
        if (trimmed is "." or ".."
            || trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || trimmed.Contains('/')
            || trimmed.Contains('\\'))
        {
            throw new ArgumentException($"Project name '{name}' is not a valid folder name.", nameof(name));
        }

        return trimmed;
    }
}
