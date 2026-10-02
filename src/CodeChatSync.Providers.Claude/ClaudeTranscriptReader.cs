using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CodeChatSync.Providers.Claude;

/// <summary>
/// Extracts the few metadata fields the provider needs from a Claude Code transcript,
/// without keeping any message content.
/// </summary>
/// <remarks>
/// <para>
/// Transcripts are JSON Lines. Only these top-level string properties are read:
/// <c>cwd</c> (first value, the session's working directory), <c>aiTitle</c> (last
/// value, the generated title) and <c>timestamp</c> (earliest and latest, when the
/// conversation started and last moved). Every other property is skipped without being
/// materialized, and malformed lines are ignored.
/// </para>
/// <para>
/// When asked for the title it also keeps the first thing the user typed, as a fallback
/// name: many sessions never get an <c>aiTitle</c>, and an archive where most chats are
/// nameless is not browsable. The first prompt is only searched for until it is found, so
/// the cost is a few early lines, not the whole file.
/// </para>
/// </remarks>
internal static class ClaudeTranscriptReader
{
    /// <summary>Lines longer than this are skipped: metadata records are small.</summary>
    private const int MaxParsedLineLength = 1024 * 1024;

    private const int MaxTitleLength = 200;

    private static readonly byte[] CwdProperty = "cwd"u8.ToArray();
    private static readonly byte[] TitleProperty = "aiTitle"u8.ToArray();
    private static readonly byte[] TimestampProperty = "timestamp"u8.ToArray();

    public static TranscriptMetadata Read(string path, bool includeTitle)
    {
        string? workingDirectory = null;
        string? title = null;
        DateTimeOffset? earliest = null;
        DateTimeOffset? latest = null;
        string? firstPrompt = null;

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line.Length > MaxParsedLineLength)
            {
                continue;
            }

            if (includeTitle && firstPrompt is null)
            {
                firstPrompt = TryReadPrompt(line);
            }

            var (lineCwd, lineTitle, lineTimestamp) = ParseLine(line);
            workingDirectory ??= lineCwd;
            if (lineTitle is not null)
            {
                title = lineTitle;
            }

            // Earliest and latest rather than first and last: bookkeeping records are not
            // guaranteed to be written in time order.
            if (lineTimestamp is { } timestamp)
            {
                earliest = earliest is null || timestamp < earliest ? timestamp : earliest;
                latest = latest is null || timestamp > latest ? timestamp : latest;
            }

            if (!includeTitle && workingDirectory is not null)
            {
                break;
            }
        }

        return includeTitle
            ? new TranscriptMetadata(workingDirectory, title, earliest, latest, firstPrompt)
            : new TranscriptMetadata(workingDirectory, null);
    }

    /// <summary>
    /// Text Claude Code puts around what the user typed — reminders, IDE state, and the
    /// records of local slash commands. A message that starts with one is not a prompt.
    /// </summary>
    private static readonly string[] InjectedPrefixes =
    [
        "<system-reminder>",
        "<ide_",
        "<command-",
        "<local-command",
        "<user-prompt-submit-hook>",
        "Caveat:"
    ];

    /// <summary>
    /// The text of a line if it is something the user typed, or <see langword="null"/>.
    /// </summary>
    private static string? TryReadPrompt(string line)
    {
        // Cheap test first: this runs on every early line, and almost none are user messages.
        if (!line.Contains("\"user\"", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !HasString(root, "type", "user")
                || IsTrue(root, "isMeta")
                || IsTrue(root, "isSidechain")
                || !root.TryGetProperty("message", out var message)
                || message.ValueKind != JsonValueKind.Object
                || !message.TryGetProperty("content", out var content))
            {
                return null;
            }

            return content.ValueKind switch
            {
                JsonValueKind.String => AsPrompt(content.GetString()),
                JsonValueKind.Array => content.EnumerateArray()
                    .Where(block => block.ValueKind == JsonValueKind.Object && HasString(block, "type", "text"))
                    .Select(block => block.TryGetProperty("text", out var text) ? AsPrompt(text.GetString()) : null)
                    .FirstOrDefault(prompt => prompt is not null),
                _ => null
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether text, already trimmed at the start, is something Claude Code put there
    /// rather than something the user typed.
    /// </summary>
    internal static bool IsInjectedText(string trimmedText) =>
        InjectedPrefixes.Any(prefix => trimmedText.StartsWith(prefix, StringComparison.Ordinal));

    private static string? AsPrompt(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.TrimStart();
        if (IsInjectedText(trimmed))
        {
            return null;
        }

        var collapsed = string.Join(' ', trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length > MaxTitleLength ? collapsed[..MaxTitleLength] : collapsed;
    }

    private static bool HasString(JsonElement element, string property, string expected) =>
        element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
        && value.GetString() == expected;

    private static bool IsTrue(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static (string? Cwd, string? Title, DateTimeOffset? Timestamp) ParseLine(string line)
    {
        string? cwd = null;
        string? title = null;
        DateTimeOffset? timestamp = null;

        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(line));
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return (null, null, null);
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var isCwd = reader.ValueTextEquals(CwdProperty);
                var isTitle = !isCwd && reader.ValueTextEquals(TitleProperty);
                var isTimestamp = !isCwd && !isTitle && reader.ValueTextEquals(TimestampProperty);

                if (!reader.Read())
                {
                    break;
                }

                if (reader.TokenType == JsonTokenType.String && isTimestamp)
                {
                    if (DateTimeOffset.TryParse(
                            reader.GetString(),
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                            out var parsed))
                    {
                        timestamp ??= parsed;
                    }
                }
                else if (reader.TokenType == JsonTokenType.String && (isCwd || isTitle))
                {
                    var value = reader.GetString();
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        continue;
                    }

                    if (isCwd)
                    {
                        cwd ??= value;
                    }
                    else
                    {
                        value = value.Trim();
                        title = value.Length > MaxTitleLength ? value[..MaxTitleLength] : value;
                    }
                }
                else
                {
                    reader.Skip();
                }
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            // A torn or non-JSON line carries no trustworthy metadata.
            return (null, null, null);
        }

        return (cwd, title, timestamp);
    }
}

internal sealed record TranscriptMetadata(
    string? WorkingDirectory,
    string? Title,
    DateTimeOffset? Earliest = null,
    DateTimeOffset? Latest = null,
    string? FirstPrompt = null);
