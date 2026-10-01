using System.Buffers;
using CodeChatSync.Providers.VisualStudio;
using MessagePack;

namespace CodeChatSync.Tests;

/// <summary>
/// A restored chat is listed from the record's header but opens empty without the
/// message values, so both are rebuilt from the transcript.
/// </summary>
public sealed class CopilotChatWindowRecordTests
{
    private const string SessionId = "11111111-2222-3333-4444-555555555555";
    private const string TemplateSessionId = "99999999-9999-9999-9999-999999999999";
    private static readonly DateTime Created = new(2026, 9, 29, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Updated = new(2026, 9, 29, 8, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void TryBuild_WritesTheVersionHeaderAndAPairOfValuesPerExchange()
    {
        var record = Build([Turn("first question", "first answer"), Turn("second question", "second answer")]);

        var values = Split(record);

        // version, header, then question and reply for each of the two exchanges.
        Assert.Equal(6, values.Count);
        Assert.Equal("1", ToJson(values[0]));
    }

    [Fact]
    public void TryBuild_PutsTheQuestionAndTheReplyWhereVisualStudioRendersThem()
    {
        var record = Build([Turn("what does this do?", "it syncs chats")]);

        var values = Split(record);

        Assert.Contains("\"Content\":\"what does this do?\"", ToJson(values[2]), StringComparison.Ordinal);
        Assert.Contains("\"Content\":\"it syncs chats\"", ToJson(values[3]), StringComparison.Ordinal);
    }

    [Fact]
    public void TryBuild_TitlesTheChatWithTheQuestionAndPreviewsTheLastReply()
    {
        var record = Build([Turn("open question", "first answer"), Turn("follow up", "final answer")]);

        var header = ToJson(Split(record)[1]);

        Assert.Contains("\"Name\":\"open question\"", header, StringComparison.Ordinal);
        Assert.Contains("\"LastMessagePreview\":\"final answer\"", header, StringComparison.Ordinal);
    }

    /// <summary>
    /// Visual Studio prefixes a question with a context block the user never typed.
    /// </summary>
    [Fact]
    public void TryBuild_LeavesTheIdeContextBlockOutOfTheTitle()
    {
        var question = "<ide_context>\r\nIDE: Visual Studio\r\n</ide_context>\r\nwhat changed?";

        var record = Build([Turn(question, "plenty")]);

        Assert.Contains("\"Name\":\"what changed?\"", ToJson(Split(record)[1]), StringComparison.Ordinal);
    }

    [Fact]
    public void TryBuild_PointsEveryPartAtTheRestoredSession()
    {
        var record = Build([Turn("q", "a")]);
        var values = Split(record);

        Assert.Contains(SessionId, ToJson(values[1]), StringComparison.Ordinal);
        Assert.Contains(SessionId, ToJson(values[3]), StringComparison.Ordinal);
        Assert.DoesNotContain(TemplateSessionId, ToJson(values[1]), StringComparison.Ordinal);
        Assert.DoesNotContain(TemplateSessionId, ToJson(values[3]), StringComparison.Ordinal);
    }

    /// <summary>
    /// Tool definitions and gathered context are the bulk of a real message and say
    /// nothing about what was said.
    /// </summary>
    [Fact]
    public void TryBuild_DropsTheToolDefinitionsAndTheGatheredContext()
    {
        var record = Build([Turn("q", "a")]);

        var question = ToJson(Split(record)[2]);

        Assert.Contains("\"Functions\":[]", question, StringComparison.Ordinal);
        Assert.Contains("\"Context\":[]", question, StringComparison.Ordinal);
        Assert.DoesNotContain("create_file", question, StringComparison.Ordinal);
    }

    /// <summary>
    /// Everything the rebuild does not set is copied byte for byte, so Visual Studio's
    /// extension types survive. A timestamp is the case that would break first.
    /// </summary>
    [Fact]
    public void TryBuild_KeepsUntouchedFieldsExactlyAsTheTemplateHadThem()
    {
        var template = Template();
        var record = CopilotChatWindowRecord.TryBuild(template, SessionId, [Turn("q", "a")]);

        var header = ToJson(Split(record!)[1]);

        Assert.Contains("\"Keep\":\"untouched\"", header, StringComparison.Ordinal);
        Assert.Contains(ToJson(Split(template)[1].ToArray()).Split("\"TimeCreated\":")[1][..10], header, StringComparison.Ordinal);
    }

    /// <summary>
    /// The list sorts and ages chats by these, so a restored one must carry the times it
    /// really had rather than the template's.
    /// </summary>
    [Fact]
    public void TryBuild_CarriesTheTimesFromThePcThatHeldTheConversation()
    {
        var started = new DateTimeOffset(2026, 9, 29, 10, 1, 55, TimeSpan.Zero);
        var ended = new DateTimeOffset(2026, 9, 29, 10, 2, 56, TimeSpan.Zero);

        var record = CopilotChatWindowRecord.TryBuild(Template(), SessionId, [Turn("q", "a")], started, ended);

        var header = ToJson(Split(record!)[1]);
        Assert.Contains("2026-09-29T10:01:55", header, StringComparison.Ordinal);
        Assert.Contains("2026-09-29T10:02:56", header, StringComparison.Ordinal);
        Assert.DoesNotContain("2026-09-29T08:00:00", header, StringComparison.Ordinal);
    }

    /// <summary>
    /// Visual Studio stores these as the MessagePack timestamp extension, not as text;
    /// writing a string instead would leave the list unable to order the chat.
    /// </summary>
    [Fact]
    public void TryBuild_KeepsTheTimestampEncodingTheTemplateUsed()
    {
        var record = CopilotChatWindowRecord.TryBuild(
            Template(), SessionId, [Turn("q", "a")], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        var reader = new MessagePackReader(Split(record!)[1]);
        var count = reader.ReadMapHeader();
        var checkedFields = 0;
        for (var i = 0; i < count; i++)
        {
            var key = reader.ReadString();
            if (key is "TimeCreated" or "TimeUpdated")
            {
                Assert.Equal(MessagePackType.Extension, reader.NextMessagePackType);
                checkedFields++;
            }

            reader.Skip();
        }

        Assert.Equal(2, checkedFields);
    }

    [Fact]
    public void TryBuild_LeavesTheTemplatesTimesWhenTheRealOnesAreUnknown()
    {
        var record = Build([Turn("q", "a")]);

        Assert.Contains("2026-09-29T08:00:00", ToJson(Split(record)[1]), StringComparison.Ordinal);
    }

    [Fact]
    public void TryBuild_RecordsAQuestionThatNeverGotAnAnswer()
    {
        var record = Build([Turn("unanswered", null)]);

        var values = Split(record);

        Assert.Equal(3, values.Count);
        Assert.Contains("\"Content\":\"unanswered\"", ToJson(values[2]), StringComparison.Ordinal);
    }

    [Fact]
    public void TryBuild_DeclinesAChatWithNothingInIt()
    {
        Assert.Null(CopilotChatWindowRecord.TryBuild(Template(), SessionId, []));
    }

    /// <summary>A Visual Studio update could change the shape: better nothing than a corrupt entry.</summary>
    [Fact]
    public void TryBuild_DeclinesATemplateItCannotRead()
    {
        Assert.Null(CopilotChatWindowRecord.TryBuild(new byte[] { 0x01 }, SessionId, [Turn("q", "a")]));
        Assert.Null(CopilotChatWindowRecord.TryBuild(new byte[] { 0xC1 }, SessionId, [Turn("q", "a")]));
    }

    private static CopilotChatTurn Turn(string question, string? answer) =>
        new(0, question, answer, "2026-09-29T09:00:00.000Z");

    private static byte[] Build(IReadOnlyList<CopilotChatTurn> turns)
    {
        var record = CopilotChatWindowRecord.TryBuild(Template(), SessionId, turns);
        Assert.NotNull(record);
        return record!;
    }

    private static string ToJson(ReadOnlyMemory<byte> value) => MessagePackSerializer.ConvertToJson(value);

    private static string ToJson(byte[] value) => MessagePackSerializer.ConvertToJson(value);

    private static List<ReadOnlyMemory<byte>> Split(byte[] record)
    {
        var values = new List<ReadOnlyMemory<byte>>();
        var rest = record.AsMemory();
        while (rest.Length > 0)
        {
            var reader = new MessagePackReader(rest);
            reader.Skip();
            values.Add(rest[..(int)reader.Consumed]);
            rest = rest[(int)reader.Consumed..];
        }

        return values;
    }

    /// <summary>A record shaped like the ones Visual Studio writes, small enough to assert on.</summary>
    private static byte[] Template()
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);

        writer.Write(1);

        writer.WriteMapHeader(6);
        writer.Write("Name");
        writer.WriteNil();
        writer.Write("Id");
        writer.WriteArrayHeader(2);
        writer.Write(TemplateSessionId);
        writer.WriteMapHeader(1);
        writer.Write("Id");
        writer.Write("Microsoft.VisualStudio.Conversations.Chat.HelpWindow");
        writer.Write("LastMessagePreview");
        writer.Write("a stale preview");
        writer.Write("TimeCreated");
        writer.Write(Created);
        writer.Write("TimeUpdated");
        writer.Write(Updated);
        writer.Write("Keep");
        writer.Write("untouched");

        WriteTemplateMessage(ref writer, kind: 0, includeFunctions: true);
        WriteTemplateMessage(ref writer, kind: 1, includeFunctions: false);

        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteTemplateMessage(ref MessagePackWriter writer, int kind, bool includeFunctions)
    {
        writer.WriteArrayHeader(2);
        writer.Write(kind);
        writer.WriteMapHeader(includeFunctions ? 6 : 5);

        writer.Write("CorrelationId");
        writer.Write(Guid.NewGuid().ToString());
        writer.Write("MessageId");
        writer.Write(Guid.NewGuid().ToString());

        writer.Write("Context");
        writer.WriteArrayHeader(1);
        writer.Write("gathered context");

        writer.Write("Content");
        writer.WriteArrayHeader(1);
        writer.WriteArrayHeader(2);
        writer.Write(3);
        writer.WriteMapHeader(1);
        writer.Write("Content");
        writer.Write("template text");

        if (includeFunctions)
        {
            writer.Write("Functions");
            writer.WriteArrayHeader(1);
            writer.Write("create_file");
        }

        writer.Write("SessionId");
        writer.WriteArrayHeader(2);
        writer.Write(TemplateSessionId);
        writer.WriteMapHeader(1);
        writer.Write("Id");
        writer.Write("Microsoft.VisualStudio.Conversations.Chat.HelpWindow");
    }
}
