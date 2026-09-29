using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeChatSync.Core;

/// <summary>
/// Per-PC record of what was last synced, used to tell a one-sided change from a
/// genuine conflict. Local-only: it is never stored in the sync repository.
/// </summary>
public sealed class SyncState
{
    [JsonInclude]
    [JsonPropertyName("entries")]
    private Dictionary<string, string> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Content hash recorded at the last successful sync, if any.</summary>
    public string? GetBaseline(string relativePath) =>
        Entries.TryGetValue(RelativePathGuard.Normalize(relativePath), out var hash) ? hash : null;

    public void SetBaseline(string relativePath, string contentHash) =>
        Entries[RelativePathGuard.Normalize(relativePath)] = contentHash;

    public void Remove(string relativePath) =>
        Entries.Remove(RelativePathGuard.Normalize(relativePath));

    /// <summary>Hashes a file's contents, returning <see langword="null"/> when it does not exist.</summary>
    public static string? ComputeHash(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static SyncState Load(string path)
    {
        if (!File.Exists(path))
        {
            return new SyncState();
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<SyncState>(json, SerializerOptions) ?? new SyncState();
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt state file must not block syncing: rebuild it from scratch.
            return new SyncState();
        }
    }

    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is { Length: > 0 })
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(this, SerializerOptions));
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        IncludeFields = false
    };
}
