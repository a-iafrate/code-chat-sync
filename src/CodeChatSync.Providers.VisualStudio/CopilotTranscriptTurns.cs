using System.Text.Json;

namespace CodeChatSync.Providers.VisualStudio;

/// <summary>One exchange of a chat, in the shape Visual Studio's chat index stores.</summary>
public sealed record CopilotChatTurn(int Index, string UserMessage, string? AssistantResponse, string? Timestamp);

/// <summary>
/// Rebuilds a session's exchanges from its <c>events.jsonl</c> transcript, so a restored
/// chat can be listed by Visual Studio.
/// </summary>
/// <remarks>
/// <para>
/// Visual Studio's chat list is built from the <c>turns</c> table, not from the session
/// folder: the entry's title is the user's message and its subtitle the assistant's
/// reply. A restored session with no rows there is complete on disk and still never
/// appears.
/// </para>
/// <para>
/// The rule was checked against a 3.2 MB transcript whose 13 turns Visual Studio had
/// already recorded itself: one turn per <c>user.message</c> event, in order, reproduced
/// all 13 user messages exactly and 11 of the 13 replies. In the other two Visual Studio
/// stored no reply where the transcript holds a partial one, which only changes the
/// preview text. The timestamp Visual Studio stores matches no event in the transcript —
/// it is when the row was written — so the turn's own time is used instead, which is what
/// the list needs in order to sort.
/// </para>
/// <para>
/// The format is undocumented: anything unrecognized is skipped rather than guessed at,
/// and a transcript that cannot be read yields no turns instead of failing the sync.
/// </para>
/// </remarks>
public static class CopilotTranscriptTurns
{
    private const string UserMessageEvent = "user.message";
    private const string AssistantMessageEvent = "assistant.message";

    /// <summary>
    /// Reads the exchanges of the transcript at <paramref name="transcriptPath"/>, or an
    /// empty list when there is nothing readable there.
    /// </summary>
    public static IReadOnlyList<CopilotChatTurn> Read(string transcriptPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transcriptPath);
        if (!File.Exists(transcriptPath))
        {
            return [];
        }

        var turns = new List<Builder>();
        try
        {
            // Streamed a line at a time: transcripts of tens of megabytes are normal.
            foreach (var line in File.ReadLines(transcriptPath))
            {
                if (line.Length == 0)
                {
                    continue;
                }

                Read(line, turns);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        return [.. turns.Select((turn, index) => new CopilotChatTurn(
            index,
            turn.UserMessage,
            turn.AssistantResponse,
            turn.AssistantTimestamp ?? turn.UserTimestamp))];
    }

    private static void Read(string line, List<Builder> turns)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(line);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return;
        }

        if (root.ValueKind is not JsonValueKind.Object
            || !root.TryGetProperty("type", out var typeProperty)
            || typeProperty.GetString() is not { Length: > 0 } type
            || !root.TryGetProperty("data", out var data)
            || data.ValueKind is not JsonValueKind.Object)
        {
            return;
        }

        var timestamp = root.TryGetProperty("timestamp", out var stamp) && stamp.ValueKind is JsonValueKind.String
            ? stamp.GetString()
            : null;
        var content = data.TryGetProperty("content", out var value) && value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;

        switch (type)
        {
            case UserMessageEvent:
                turns.Add(new Builder { UserMessage = content ?? string.Empty, UserTimestamp = timestamp });
                break;

            // The assistant can speak several times per turn, splitting a long reply or
            // narrating tool use. The list shows the last thing it said.
            case AssistantMessageEvent when turns.Count > 0 && content is { Length: > 0 }:
                turns[^1].AssistantResponse = content;
                turns[^1].AssistantTimestamp = timestamp;
                break;
        }
    }

    private sealed class Builder
    {
        public required string UserMessage { get; init; }

        public string? UserTimestamp { get; init; }

        public string? AssistantResponse { get; set; }

        public string? AssistantTimestamp { get; set; }
    }
}
