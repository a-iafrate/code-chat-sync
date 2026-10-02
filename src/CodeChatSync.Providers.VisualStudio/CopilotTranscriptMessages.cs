using System.Globalization;
using System.Text;
using System.Text.Json;
using CodeChatSync.Core;

namespace CodeChatSync.Providers.VisualStudio;

/// <summary>
/// Reads the conversation out of a session's <c>events.jsonl</c>, for display.
/// </summary>
/// <remarks>
/// <para>
/// A transcript is JSON Lines of typed events. Only <c>user.message</c> and
/// <c>assistant.message</c> carry the conversation; everything else — tool starts and
/// results, hooks, permission prompts — is skipped, and is most of the file: a 46 MB
/// transcript held 642 user messages and 8,308 assistant events, of which only 2,778 had
/// any text (the rest were tool requests). A message is kept as the user typed it, so the
/// <c>&lt;ide_context&gt;</c> block Visual Studio prepends is removed.
/// </para>
/// <para>
/// Streamed a line at a time and never held whole in memory. A cheap substring test comes
/// before any parsing, since nearly every line is some other event, and the event's type
/// is confirmed after parsing because the same words can appear inside tool output.
/// </para>
/// </remarks>
internal static class CopilotTranscriptMessages
{
    /// <summary>Lines longer than this are skipped rather than parsed.</summary>
    private const int MaximumLineLength = 8 * 1024 * 1024;

    private const string UserEvent = "user.message";
    private const string AssistantEvent = "assistant.message";

    public static void ReadInto(string transcriptPath, ArchivedChatContentBuilder builder)
    {
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

            if (!line.Contains(UserEvent, StringComparison.Ordinal)
                && !line.Contains(AssistantEvent, StringComparison.Ordinal))
            {
                continue;
            }

            TryAdd(line, builder);
        }
    }

    private static void TryAdd(string line, ArchivedChatContentBuilder builder)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.String)
            {
                return;
            }

            var text = content.GetString();
            switch (type.GetString())
            {
                case UserEvent when text is not null:
                    builder.Add(ChatRole.User, CopilotChatTitle.StripIdeContext(text), ReadTimestamp(root));
                    break;
                case AssistantEvent:
                    builder.Add(ChatRole.Assistant, text, ReadTimestamp(root));
                    break;
            }
        }
        catch (JsonException)
        {
            // A torn or non-JSON line holds nothing trustworthy to show.
        }
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement root) =>
        root.TryGetProperty("timestamp", out var value)
        && value.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(
            value.GetString(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
}
