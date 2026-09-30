using CodeChatSync.Providers.VisualStudio;
using CodeChatSync.Tests.TestSupport;

namespace CodeChatSync.Tests;

/// <summary>
/// Visual Studio renders its chat list from the exchanges, so a restored chat only
/// appears once they have been rebuilt from its transcript.
/// </summary>
public sealed class CopilotTranscriptTurnsTests : IDisposable
{
    private readonly TempDirectory _root = new();

    public void Dispose() => _root.Dispose();

    [Fact]
    public void Read_PairsEachUserMessageWithTheAssistantReplyThatFollows()
    {
        var path = WriteTranscript(
            Event("session.start", "{}", "2026-09-29T09:00:00.000Z"),
            Event("user.message", Content("first question"), "2026-09-29T09:00:01.000Z"),
            Event("assistant.message", Content("first answer"), "2026-09-29T09:00:02.000Z"),
            Event("user.message", Content("second question"), "2026-09-29T09:05:00.000Z"),
            Event("assistant.message", Content("second answer"), "2026-09-29T09:05:04.000Z"));

        var turns = CopilotTranscriptTurns.Read(path);

        Assert.Collection(
            turns,
            turn =>
            {
                Assert.Equal(0, turn.Index);
                Assert.Equal("first question", turn.UserMessage);
                Assert.Equal("first answer", turn.AssistantResponse);
                Assert.Equal("2026-09-29T09:00:02.000Z", turn.Timestamp);
            },
            turn =>
            {
                Assert.Equal(1, turn.Index);
                Assert.Equal("second question", turn.UserMessage);
                Assert.Equal("second answer", turn.AssistantResponse);
            });
    }

    /// <summary>
    /// The assistant speaks several times per turn while it narrates tool use; the list
    /// shows the last thing it said.
    /// </summary>
    [Fact]
    public void Read_KeepsTheLastReplyOfATurnThatUsedTools()
    {
        var path = WriteTranscript(
            Event("user.message", Content("do the thing"), "2026-09-29T09:00:01.000Z"),
            Event("assistant.message", Content("Looking into it"), "2026-09-29T09:00:02.000Z"),
            Event("tool.execution_start", "{}", "2026-09-29T09:00:03.000Z"),
            Event("tool.execution_complete", "{}", "2026-09-29T09:00:04.000Z"),
            Event("assistant.message", Content("Done."), "2026-09-29T09:00:05.000Z"));

        var turn = Assert.Single(CopilotTranscriptTurns.Read(path));

        Assert.Equal("Done.", turn.AssistantResponse);
        Assert.Equal("2026-09-29T09:00:05.000Z", turn.Timestamp);
    }

    [Fact]
    public void Read_ReportsAnUnansweredQuestionWithoutAReply()
    {
        var path = WriteTranscript(
            Event("user.message", Content("question left hanging"), "2026-09-29T09:00:01.000Z"));

        var turn = Assert.Single(CopilotTranscriptTurns.Read(path));

        Assert.Null(turn.AssistantResponse);
        Assert.Equal("2026-09-29T09:00:01.000Z", turn.Timestamp);
    }

    [Fact]
    public void Read_IgnoresAssistantOutputBeforeAnyQuestion()
    {
        var path = WriteTranscript(
            Event("system.message", Content("boot"), "2026-09-29T09:00:00.000Z"),
            Event("assistant.message", Content("stray"), "2026-09-29T09:00:01.000Z"));

        Assert.Empty(CopilotTranscriptTurns.Read(path));
    }

    /// <summary>The format is undocumented, so unreadable lines must not fail a sync.</summary>
    [Fact]
    public void Read_SkipsBlankAndMalformedLinesAndKeepsTheRest()
    {
        var path = _root.WriteFile(
            "broken/events.jsonl",
            string.Join(
                '\n',
                string.Empty,
                "{ this is not json",
                "\"a bare string\"",
                Event("user.message", Content("still counted"), "2026-09-29T09:00:01.000Z"),
                "{\"type\":\"user.message\"}"));

        var turn = Assert.Single(CopilotTranscriptTurns.Read(path));

        Assert.Equal("still counted", turn.UserMessage);
    }

    [Fact]
    public void Read_ReturnsNothingForAChatWithNoTranscript()
    {
        Assert.Empty(CopilotTranscriptTurns.Read(_root.Combine("absent", "events.jsonl")));
    }

    private string WriteTranscript(params string[] lines) =>
        _root.WriteFile($"{Guid.NewGuid():N}/events.jsonl", string.Join('\n', lines) + "\n");

    private static string Event(string type, string data, string timestamp) =>
        $"{{\"type\":\"{type}\",\"data\":{data},\"timestamp\":\"{timestamp}\"}}";

    private static string Content(string value) => $"{{\"content\":\"{value}\"}}";
}
