using System.Globalization;
using System.Text;
using System.Text.Json;
using CodeChatSync.Core;

namespace CodeChatSync.Providers.Claude;

/// <summary>
/// Reads the conversation out of a Claude Code transcript, for display.
/// </summary>
/// <remarks>
/// <para>
/// Only what was said is kept: the text blocks of <c>user</c> and <c>assistant</c> records.
/// Tool calls, their results and the model's thinking are skipped — they are most of the
/// file — along with records Claude Code writes on its own account: meta lines, subagent
/// chatter, and text it injects around the user's message (reminders, IDE state, the record
/// of a local slash command). A record's text blocks are joined into one message.
/// </para>
/// <para>
/// An archived transcript stores the project's root as a portable token, so that a chat
/// restored on another PC points at that PC's copy. Shown as it is, the token would sit
/// in the middle of a path; it is replaced with this PC's folder when that is known, and
/// with a plain <c>&lt;project&gt;</c> when it is not.
/// </para>
/// </remarks>
internal static class ClaudeTranscriptMessages
{
    /// <summary>Lines longer than this are skipped rather than parsed.</summary>
    private const int MaximumLineLength = 8 * 1024 * 1024;

    private const string UnknownProjectRoot = "<project>";

    public static void ReadInto(string transcriptPath, string? projectRoot, ArchivedChatContentBuilder builder)
    {
        var root = string.IsNullOrWhiteSpace(projectRoot) ? UnknownProjectRoot : projectRoot;

        using var stream = new FileStream(
            transcriptPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line.Length > MaximumLineLength)
            {
                continue;
            }

            // Cheap test before any parsing: most lines are neither.
            if (!line.Contains("\"user\"", StringComparison.Ordinal)
                && !line.Contains("\"assistant\"", StringComparison.Ordinal))
            {
                continue;
            }

            TryAdd(line, root, builder);
        }
    }

    private static void TryAdd(string line, string projectRoot, ArchivedChatContentBuilder builder)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var record = document.RootElement;
            if (record.ValueKind != JsonValueKind.Object
                || IsTrue(record, "isMeta")
                || IsTrue(record, "isSidechain")
                || !record.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String
                || !record.TryGetProperty("message", out var message)
                || message.ValueKind != JsonValueKind.Object
                || !message.TryGetProperty("content", out var content))
            {
                return;
            }

            var role = type.GetString() switch
            {
                "user" => ChatRole.User,
                "assistant" => ChatRole.Assistant,
                _ => (ChatRole?)null
            };

            if (role is not { } known)
            {
                return;
            }

            var text = ReadText(content, known);
            if (text is not null)
            {
                builder.Add(
                    known,
                    text.Replace(ClaudeTranscriptPathMapper.ProjectRootToken, projectRoot, StringComparison.Ordinal),
                    ReadTimestamp(record));
            }
        }
        catch (JsonException)
        {
            // A torn or non-JSON line holds nothing trustworthy to show.
        }
    }

    /// <summary>The text of a record's content, or <see langword="null"/> when it has none to show.</summary>
    private static string? ReadText(JsonElement content, ChatRole role)
    {
        switch (content.ValueKind)
        {
            case JsonValueKind.String:
                return Keep(content.GetString(), role);

            case JsonValueKind.Array:
                var parts = content.EnumerateArray()
                    .Where(block => block.ValueKind == JsonValueKind.Object
                        && block.TryGetProperty("type", out var blockType)
                        && blockType.ValueKind == JsonValueKind.String
                        && blockType.GetString() == "text"
                        && block.TryGetProperty("text", out var text)
                        && text.ValueKind == JsonValueKind.String)
                    .Select(block => Keep(block.GetProperty("text").GetString(), role))
                    .Where(text => text is not null)
                    .ToArray();
                return parts.Length == 0 ? null : string.Join(Environment.NewLine + Environment.NewLine, parts);

            default:
                return null;
        }
    }

    /// <summary>
    /// Drops what Claude Code injected into a user turn; the assistant's text is always its own.
    /// </summary>
    private static string? Keep(string? text, ChatRole role)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return role == ChatRole.User && ClaudeTranscriptReader.IsInjectedText(text.TrimStart()) ? null : text;
    }

    private static bool IsTrue(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static DateTimeOffset? ReadTimestamp(JsonElement record) =>
        record.TryGetProperty("timestamp", out var value)
        && value.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(
            value.GetString(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
}
