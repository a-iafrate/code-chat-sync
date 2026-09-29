namespace CodeChatSync.Core;

/// <summary>
/// Stores the per-user "start with Windows" entries.
/// </summary>
/// <remarks>
/// Declared here so the auto-start rules stay testable and platform-agnostic; the
/// Windows registry implementation lives in the app.
/// </remarks>
public interface IStartupEntryStore
{
    /// <summary>The command registered under <paramref name="name"/>, if any.</summary>
    string? Read(string name);

    void Write(string name, string command);

    void Remove(string name);
}

/// <summary>
/// Turns launching the tray app at sign-in on and off.
/// </summary>
/// <remarks>
/// A per-user entry is used rather than a machine-wide one or a scheduled task: it
/// needs no elevation, and it only affects the user whose chats are being synced.
/// </remarks>
public sealed class AutoStartManager(IStartupEntryStore store, string? entryName = null)
{
    public const string DefaultEntryName = "CodeChatSync";

    private readonly IStartupEntryStore _store = store ?? throw new ArgumentNullException(nameof(store));

    private readonly string _entryName = entryName switch
    {
        null => DefaultEntryName,
        { Length: > 0 } name when !string.IsNullOrWhiteSpace(name) => name,
        _ => throw new ArgumentException("An entry name is required.", nameof(entryName))
    };

    /// <summary>Whether <paramref name="executablePath"/> is the registered command.</summary>
    /// <remarks>
    /// An entry pointing at a different build, for example one left behind by an
    /// older install, counts as disabled so enabling it repairs the entry.
    /// </remarks>
    public bool IsEnabled(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        if (_store.Read(_entryName) is not { Length: > 0 } command)
        {
            return false;
        }

        return PathsMatch(Unquote(command), executablePath);
    }

    public void Enable(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        _store.Write(_entryName, FormatCommand(executablePath));
    }

    public void Disable() => _store.Remove(_entryName);

    /// <summary>Applies <paramref name="enabled"/> and reports the resulting state.</summary>
    public bool Set(bool enabled, string executablePath)
    {
        if (enabled)
        {
            Enable(executablePath);
        }
        else
        {
            Disable();
        }

        return enabled;
    }

    /// <summary>
    /// Quotes the path so a folder containing spaces still starts correctly.
    /// </summary>
    public static string FormatCommand(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        var fullPath = Path.GetFullPath(executablePath);
        return fullPath.Contains('"') ? fullPath : $"\"{fullPath}\"";
    }

    private static string Unquote(string command)
    {
        var trimmed = command.Trim();
        return trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"'
            ? trimmed[1..^1]
            : trimmed;
    }

    private static bool PathsMatch(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // A malformed leftover entry is simply not this executable.
            return false;
        }
    }
}
