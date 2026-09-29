using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeChatSync.Core;

/// <summary>A project registered on this PC, stored by remote rather than by path.</summary>
public sealed record LocalProjectEntry
{
    /// <summary>Normalized Git remote identifying the project across PCs.</summary>
    [JsonPropertyName("remote")]
    public required string Remote { get; init; }

    /// <summary>Where the project currently lives on this PC.</summary>
    [JsonPropertyName("localPath")]
    public required string LocalPath { get; init; }
}

/// <summary>
/// Per-PC configuration: the sync folder and the local path of each registered
/// project. It is machine-specific and must never be committed to the sync
/// repository.
/// </summary>
public sealed class LocalConfig
{
    [JsonPropertyName("syncRootPath")]
    public string? SyncRootPath { get; set; }

    [JsonPropertyName("projects")]
    public List<LocalProjectEntry> Projects { get; set; } = [];

    /// <summary>
    /// Overrides where this PC's configuration and baselines are stored. Set it to
    /// keep a run — an end-to-end test, or a portable install — away from the
    /// current user's real configuration.
    /// </summary>
    public const string HomeEnvironmentVariable = "CODECHATSYNC_HOME";

    /// <summary>Default location of this file for the current user.</summary>
    public static string GetDefaultPath() =>
        GetHome() is { Length: > 0 } home
            ? Path.Combine(home, "local-config.json")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "CodeChatSync",
                "local-config.json");

    /// <summary>Directory holding per-PC sync baselines, which are also never committed.</summary>
    public static string GetDefaultStateDirectory() =>
        GetHome() is { Length: > 0 } home
            ? Path.Combine(home, "state")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CodeChatSync",
                "state");

    private static string? GetHome()
    {
        var configured = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
        return string.IsNullOrWhiteSpace(configured) ? null : Path.GetFullPath(configured.Trim());
    }

    public static LocalConfig Load(string? path = null)
    {
        var configPath = path ?? GetDefaultPath();
        if (!File.Exists(configPath))
        {
            return new LocalConfig();
        }

        try
        {
            return JsonSerializer.Deserialize<LocalConfig>(File.ReadAllText(configPath), ConfigJson.Options)
                ?? new LocalConfig();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Local configuration at '{configPath}' is not valid JSON.", exception);
        }
    }

    public void Save(string? path = null)
    {
        var configPath = path ?? GetDefaultPath();
        var directory = Path.GetDirectoryName(Path.GetFullPath(configPath));
        if (directory is { Length: > 0 })
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(configPath, JsonSerializer.Serialize(this, ConfigJson.Options));
    }

    /// <summary>Finds the entry matching a remote, ignoring how the remote was written.</summary>
    public LocalProjectEntry? Find(ProjectIdentity identity)
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
    /// Registers a project, or updates its local path when it moved on this PC.
    /// </summary>
    /// <returns><see langword="true"/> when the project was not registered before.</returns>
    public bool AddOrUpdate(ProjectIdentity identity, string localPath)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);

        var fullPath = Path.GetFullPath(localPath);
        var existing = Find(identity);
        if (existing is not null)
        {
            Projects.Remove(existing);
            Projects.Add(existing with { Remote = identity.NormalizedRemote, LocalPath = fullPath });
            return false;
        }

        Projects.Add(new LocalProjectEntry { Remote = identity.NormalizedRemote, LocalPath = fullPath });
        return true;
    }
}

internal static class ConfigJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
