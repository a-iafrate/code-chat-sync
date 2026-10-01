using System.Buffers;
using System.Globalization;
using MessagePack;

namespace CodeChatSync.Providers.VisualStudio;

/// <summary>
/// Rebuilds the record Visual Studio's Chat window keeps for a conversation, so a
/// restored chat can be listed and read even when the PC that produced it never had one.
/// </summary>
/// <remarks>
/// <para>
/// The record is a sequence of MessagePack values: a version marker, a header, then one
/// value per message, each shaped <c>[kind, {…}]</c> with <c>0</c> for a question and
/// <c>1</c> for a reply. The header alone makes the chat appear in the list but opens
/// empty — verified directly — because the conversation lives in the message values.
/// </para>
/// <para>
/// Those values look far heavier than they are. In a measured example the question
/// carried 18,440 characters of tool definitions and 2,495 of gathered IDE context around
/// 141 characters of actual text, and a reply's bulk was tool-call blocks. What is said
/// lives in content blocks of kind <c>3</c>. Dropping the rest turned a 46 MB transcript
/// into a 298 KB record holding all 84 exchanges.
/// </para>
/// <para>
/// Nothing here is invented: an existing record from this PC is the template, and every
/// field other than the identifiers and the text is copied byte for byte, so Visual
/// Studio's own extension types and typed containers survive untouched. That makes a
/// template mandatory — a PC that has never opened a chat has no shape to copy — and
/// keeps this honest about being best-effort against an undocumented format.
/// </para>
/// <para>
/// Tool-call blocks are deliberately not rebuilt. They record file edits, confirmations
/// and execution results; reproducing them would mean inventing state that never
/// happened on this PC, and reading a past conversation does not need them.
/// </para>
/// </remarks>
public static class CopilotChatWindowRecord
{
    /// <summary>Content block holding prose.</summary>
    private const int TextBlockKind = 3;

    /// <summary>Longest title and preview kept in the header, which only has to fit a list row.</summary>
    private const int TitleLength = 120;
    private const int PreviewLength = 300;

    /// <summary>
    /// Builds a record for <paramref name="sessionId"/> from <paramref name="turns"/>, or
    /// <see langword="null"/> when the template cannot be read or there is nothing to show.
    /// </summary>
    /// <param name="createdAt">
    /// When the chat started on the PC that produced it, so the list shows its real age
    /// rather than the template's. Left as the template had it when not known.
    /// </param>
    /// <param name="updatedAt">When the chat was last active on that PC.</param>
    public static byte[]? TryBuild(
        ReadOnlyMemory<byte> template,
        string sessionId,
        IReadOnlyList<CopilotChatTurn> turns,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? updatedAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(turns);
        if (turns.Count == 0)
        {
            return null;
        }

        try
        {
            var values = Split(template);
            if (values.Count < 4)
            {
                // A template needs a version marker, a header, a question and a reply.
                return null;
            }

            var title = Summarize(turns[0].UserMessage, TitleLength);
            var lastReply = turns.LastOrDefault(turn => turn.AssistantResponse is { Length: > 0 })?.AssistantResponse;
            var preview = Summarize(lastReply ?? turns[0].UserMessage, PreviewLength);

            var buffer = new ArrayBufferWriter<byte>();
            var writer = new MessagePackWriter(buffer);
            writer.WriteRaw(values[0].Span);
            WriteHeader(ref writer, values[1], sessionId, title, preview, createdAt, updatedAt);

            foreach (var turn in turns)
            {
                var correlationId = Guid.NewGuid().ToString();
                WriteMessage(ref writer, values[2], sessionId, correlationId, Trim(turn.UserMessage));
                if (turn.AssistantResponse is { Length: > 0 } reply)
                {
                    WriteMessage(ref writer, values[3], sessionId, correlationId, reply);
                }
            }

            writer.Flush();
            return buffer.WrittenSpan.ToArray();
        }
        catch (MessagePackSerializationException)
        {
            // A Visual Studio update changed the record's shape: better no entry than a
            // corrupt one.
            return null;
        }
        catch (EndOfStreamException)
        {
            return null;
        }
    }

    private static List<ReadOnlyMemory<byte>> Split(ReadOnlyMemory<byte> template)
    {
        var values = new List<ReadOnlyMemory<byte>>();
        var rest = template;
        while (rest.Length > 0)
        {
            var reader = new MessagePackReader(rest);
            reader.Skip();
            var consumed = (int)reader.Consumed;
            values.Add(rest[..consumed]);
            rest = rest[consumed..];
        }

        return values;
    }

    private static void WriteHeader(
        ref MessagePackWriter writer,
        ReadOnlyMemory<byte> header,
        string sessionId,
        string title,
        string preview,
        DateTimeOffset? createdAt,
        DateTimeOffset? updatedAt)
    {
        var reader = new MessagePackReader(header);
        var count = reader.ReadMapHeader();
        writer.WriteMapHeader(count);

        for (var i = 0; i < count; i++)
        {
            var key = reader.ReadString() ?? string.Empty;
            writer.Write(key);

            switch (key)
            {
                case "Name":
                    reader.Skip();
                    writer.Write(title);
                    break;
                case "LastMessagePreview":
                    reader.Skip();
                    writer.Write(preview);
                    break;
                case "Id":
                    ReplaceFirstItem(ref reader, ref writer, sessionId);
                    break;
                case "TimeCreated":
                    WriteTimestamp(ref reader, ref writer, createdAt);
                    break;
                case "TimeUpdated":
                    WriteTimestamp(ref reader, ref writer, updatedAt);
                    break;
                default:
                    CopyRaw(ref reader, ref writer);
                    break;
            }
        }
    }

    /// <summary>
    /// Writes a chat's real time, keeping whatever representation the template used.
    /// </summary>
    /// <remarks>
    /// Visual Studio was observed storing these as the MessagePack timestamp extension
    /// rather than text, so the encoding is taken from the template instead of assumed:
    /// a plain string in its place would leave the list unable to order the chat.
    /// </remarks>
    private static void WriteTimestamp(ref MessagePackReader reader, ref MessagePackWriter writer, DateTimeOffset? value)
    {
        if (value is not { } timestamp)
        {
            CopyRaw(ref reader, ref writer);
            return;
        }

        var type = reader.NextMessagePackType;
        reader.Skip();

        if (type is MessagePackType.String)
        {
            writer.Write(timestamp.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture));
            return;
        }

        writer.Write(timestamp.UtcDateTime);
    }

    private static void WriteMessage(
        ref MessagePackWriter writer,
        ReadOnlyMemory<byte> message,
        string sessionId,
        string correlationId,
        string text)
    {
        var reader = new MessagePackReader(message);
        var items = reader.ReadArrayHeader();
        writer.WriteArrayHeader(items);
        writer.Write(reader.ReadInt32());

        var count = reader.ReadMapHeader();
        writer.WriteMapHeader(count);

        for (var i = 0; i < count; i++)
        {
            var key = reader.ReadString() ?? string.Empty;
            writer.Write(key);

            switch (key)
            {
                case "CorrelationId":
                    reader.Skip();
                    writer.Write(correlationId);
                    break;
                case "MessageId":
                    reader.Skip();
                    writer.Write(Guid.NewGuid().ToString());
                    break;
                case "Content":
                    reader.Skip();
                    WriteTextBlock(ref writer, text);
                    break;

                // Tool definitions and the IDE context gathered at the time: tens of
                // kilobytes that say nothing about what was said.
                case "Functions":
                case "Context":
                    reader.Skip();
                    writer.WriteArrayHeader(0);
                    break;
                case "SessionId":
                    ReplaceFirstItem(ref reader, ref writer, sessionId);
                    break;
                default:
                    CopyRaw(ref reader, ref writer);
                    break;
            }
        }
    }

    /// <summary>One block of prose, the shape Visual Studio uses for plain text.</summary>
    private static void WriteTextBlock(ref MessagePackWriter writer, string text)
    {
        writer.WriteArrayHeader(1);
        writer.WriteArrayHeader(2);
        writer.Write(TextBlockKind);
        writer.WriteMapHeader(5);
        writer.Write("Id");
        writer.Write(Guid.NewGuid().ToByteArray());
        writer.Write("Visibility");
        writer.Write(3);
        writer.Write("Annotations");
        writer.WriteArrayHeader(0);
        writer.Write("Content");
        writer.Write(text);
        writer.Write("Mentions");
        writer.WriteArrayHeader(0);
    }

    /// <summary>Replaces the first element of an array, keeping the rest verbatim.</summary>
    private static void ReplaceFirstItem(ref MessagePackReader reader, ref MessagePackWriter writer, string replacement)
    {
        var items = reader.ReadArrayHeader();
        writer.WriteArrayHeader(items);
        reader.Skip();
        writer.Write(replacement);
        for (var item = 1; item < items; item++)
        {
            CopyRaw(ref reader, ref writer);
        }
    }

    private static void CopyRaw(ref MessagePackReader reader, ref MessagePackWriter writer)
    {
        var start = reader.Position;
        reader.Skip();
        foreach (var segment in reader.Sequence.Slice(start, reader.Position))
        {
            writer.WriteRaw(segment.Span);
        }
    }

    /// <summary>
    /// Visual Studio prefixes a question with an <c>&lt;ide_context&gt;</c> block; the list
    /// shows what the user actually typed.
    /// </summary>
    private static string Trim(string text)
    {
        const string marker = "</ide_context>";
        var end = text.IndexOf(marker, StringComparison.Ordinal);
        return (end >= 0 ? text[(end + marker.Length)..] : text).Trim();
    }

    private static string Summarize(string text, int length)
    {
        var single = Trim(text).ReplaceLineEndings(" ");
        while (single.Contains("  ", StringComparison.Ordinal))
        {
            single = single.Replace("  ", " ", StringComparison.Ordinal);
        }

        return single.Length <= length ? single : single[..length];
    }
}
