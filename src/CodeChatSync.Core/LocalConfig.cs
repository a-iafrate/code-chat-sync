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

    /// <summary>Providers enabled for this project. Null keeps legacy Visual Studio-only behavior.</summary>
    [JsonPropertyName("providerIds")]
    public List<string>? ProviderIds { get; init; }
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

/// <summary>Appearance of the configuration window on this PC.</summary>
public enum ThemePreference
{
    System,
    Light,
    Dark
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

    [JsonPropertyName("themePreference")]
    [JsonConverter(typeof(JsonStringEnumConverter<ThemePreference>))]
    public ThemePreference ThemePreference { get; set; } = ThemePreference.System;

    /// <summary>Whether the first-run setup wizard was finished or skipped on this PC.</summary>
    [JsonPropertyName("onboardingWizardSeen")]
    public bool OnboardingWizardSeen { get; set; }

    /// <summary>Whether the user hid the "Get started" checklist on this PC.</summary>
    [JsonPropertyName("gettingStartedDismissed")]
    public bool GettingStartedDismissed { get; set; }

    /// <summary>
    /// Providers whose "tool is running" check is skipped on this PC, at the user's
    /// explicit request. Their chat data is then read and written even while the
    /// tool is open, which risks torn or lost transcripts; local backups still apply.
    /// </summary>
    [JsonPropertyName("skipRunningCheckProviderIds")]
    public List<string> SkipRunningCheckProviderIds { get; set; } = [];

    public bool IsRunningCheckSkipped(string providerId) =>
        SkipRunningCheckProviderIds.Contains(providerId, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Overrides
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

    /// <summary>
    /// Directory holding safety copies of provider-owned local data this PC modifies,
    /// such as a tool's own chat index.
    /// </summary>
    /// <remarks>
    /// Deliberately outside the sync folder, unlike the backups taken before a chat file
    /// is overwritten: a tool's index covers every repository on this PC, so keeping a
    /// copy of it inside the sync repository would put unrelated clients' data there even
    /// though Git ignores it.
    /// </remarks>
    public static string GetDefaultBackupDirectory() =>
        GetHome() is { Length: > 0 } home
            ? Path.Combine(home, "backups")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CodeChatSync",
                "backups");

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
    public bool AddOrUpdate(
        ProjectIdentity identity,
        string localPath,
        string providerId = DefaultProviderId)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);

        var normalizedProviderId = NormalizeProviderId(providerId);
        var fullPath = Path.GetFullPath(localPath);
        var existing = Find(identity);
        if (existing is not null)
        {
            var providerIds = GetEnabledProviderIds(existing).ToList();
            if (!providerIds.Contains(normalizedProviderId, StringComparer.OrdinalIgnoreCase))
            {
                providerIds.Add(normalizedProviderId);
            }

            Projects.Remove(existing);
            Projects.Add(existing with
            {
                Remote = identity.NormalizedRemote,
                LocalPath = fullPath,
                ProviderIds = providerIds
            });
            return false;
        }

        Projects.Add(new LocalProjectEntry
        {
            Remote = identity.NormalizedRemote,
            LocalPath = fullPath,
            ProviderIds = [normalizedProviderId]
        });
        return true;
    }

    public const string DefaultProviderId = "visualstudio";

    /// <summary>Null provider lists in older configs mean Visual Studio only.</summary>
    public static IReadOnlyList<string> GetEnabledProviderIds(LocalProjectEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.ProviderIds ?? [DefaultProviderId];
    }

    private static string NormalizeProviderId(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        var normalized = providerId.Trim().ToLowerInvariant();
        if (normalized is "." or ".." || normalized.Any(character =>
            !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.'))
        {
            throw new ArgumentException("Provider IDs must be safe folder names.", nameof(providerId));
        }

        return normalized;
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
