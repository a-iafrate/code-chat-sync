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

/// <summary>Sessions selected for restore on this PC; an absent entry means restore all.</summary>
public sealed record LocalRestoreSelection
{
    [JsonPropertyName("providerId")]
    public required string ProviderId { get; init; }

    [JsonPropertyName("remote")]
    public required string Remote { get; init; }

    [JsonPropertyName("sessionIds")]
    public required List<string> SessionIds { get; init; }
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

    [JsonPropertyName("restoreSelections")]
    public List<LocalRestoreSelection> RestoreSelections { get; set; } = [];

    [JsonPropertyName("automaticSyncOnProviderClose")]
    public bool AutomaticSyncOnProviderClose { get; set; } = true;

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

    /// <summary>Null means restore every session, including newly discovered ones.</summary>
    public LocalRestoreSelection? FindRestoreSelection(string providerId, ProjectIdentity identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(identity);
        return RestoreSelections.FirstOrDefault(entry =>
            string.Equals(entry.ProviderId, providerId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(entry.Remote, identity.NormalizedRemote, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Select sessions to restore on this PC; null restores all. This never deletes
    /// existing local chats and never removes anything from the sync repository.
    /// </summary>
    public void SetRestoreSelection(string providerId, ProjectIdentity identity, IEnumerable<string>? sessionIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(identity);

        List<string>? selected = null;
        if (sessionIds is not null)
        {
            selected = [];
            foreach (var id in sessionIds)
            {
                if (string.IsNullOrWhiteSpace(id) || id is "." or ".." || id.Contains('/')
                    || id.Contains('\\') || id.Contains(':'))
                {
                    throw new ArgumentException("Session IDs must be single folder names.", nameof(sessionIds));
                }

                if (!selected.Contains(id, StringComparer.OrdinalIgnoreCase))
                {
                    selected.Add(id);
                }
            }
        }

        RestoreSelections.RemoveAll(entry =>
            string.Equals(entry.ProviderId, providerId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(entry.Remote, identity.NormalizedRemote, StringComparison.OrdinalIgnoreCase));
        if (selected is not null)
        {
            RestoreSelections.Add(new LocalRestoreSelection
            {
                ProviderId = providerId,
                Remote = identity.NormalizedRemote,
                SessionIds = selected
            });
        }
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
