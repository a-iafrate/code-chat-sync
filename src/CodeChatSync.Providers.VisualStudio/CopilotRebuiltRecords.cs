using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeChatSync.Core;

namespace CodeChatSync.Providers.VisualStudio;

/// <summary>
/// Remembers which Chat window records this PC rebuilt, and exactly what it wrote.
/// </summary>
/// <remarks>
/// <para>
/// A rebuilt record is derived from a transcript that is already synced, so publishing it
/// would store the same conversation twice and — because every rebuild mints fresh
/// message identifiers — two PCs rebuilding the same chat produce different bytes, which
/// the sync would report as a conflict on content nobody wrote. Measured on one chat: two
/// rebuilds of identical length differing in 452 bytes.
/// </para>
/// <para>
/// The recorded hash is what tells a rebuild still owned by the tool from one Visual
/// Studio has taken over. Continuing a restored chat makes Visual Studio rewrite the
/// record with the real thing, tool calls included; from that moment it is no longer
/// derived and has to be published like any other chat file, or the other PC would never
/// see the continuation.
/// </para>
/// <para>
/// Per-PC and never committed, like the sync baselines it sits beside: it describes what
/// this machine generated, which is meaningless anywhere else.
/// </para>
/// </remarks>
public sealed class CopilotRebuiltRecords
{
    private readonly string _path;
    private readonly Dictionary<string, string> _hashes;

    private CopilotRebuiltRecords(string path, Dictionary<string, string> hashes)
    {
        _path = path;
        _hashes = hashes;
    }

    /// <summary>Where this PC's record of its own rebuilds lives for one project.</summary>
    public static string GetDefaultPath(string providerId, ProjectInfo project)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(project);

        return Path.Combine(
            LocalConfig.GetDefaultStateDirectory(),
            providerId,
            $"{project.SyncFolderName}.chat-window.json");
    }

    public static CopilotRebuiltRecords Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return new CopilotRebuiltRecords(path, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }

        try
        {
            var stored = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            return new CopilotRebuiltRecords(
                path,
                new Dictionary<string, string>(stored ?? [], StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            // Losing this only means a rebuilt record is treated as Visual Studio's own.
            return new CopilotRebuiltRecords(path, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }
    }

    /// <summary>Whether the record now on disk is still exactly the one this PC rebuilt.</summary>
    public bool IsStillOurs(string sessionId, string recordPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return _hashes.TryGetValue(sessionId, out var expected)
            && Hash(recordPath) is { } actual
            && string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Notes a record this PC has just rebuilt.</summary>
    public void Remember(string sessionId, ReadOnlySpan<byte> content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        _hashes[sessionId] = Convert.ToHexString(SHA256.HashData(content));
    }

    /// <summary>Forgets a record, once Visual Studio owns it or it is gone.</summary>
    public bool Forget(string sessionId) => _hashes.Remove(sessionId);

    public void Save()
    {
        var directory = Path.GetDirectoryName(_path);
        if (directory is { Length: > 0 })
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(_path, JsonSerializer.Serialize(_hashes, SerializerOptions));
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static string? Hash(string path)
    {
        try
        {
            return File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
