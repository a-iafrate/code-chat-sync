using System.Text.Json;
using CodeChatSync.Core;
using CodeChatSync.Providers.Claude;
using CodeChatSync.Providers.VisualStudio;
using CodeChatSync.Tests.TestSupport;

namespace CodeChatSync.Tests;

/// <summary>
/// What the viewer shows of a conversation for each provider. A transcript is mostly tool
/// output, so the readers must find the few lines that are the conversation and leave the
/// rest — without mistaking a tool's output for a message because it happens to contain the
/// same words.
/// </summary>
public sealed class ArchivedChatReaderTests : IDisposable
{
    private const string SessionId = "11111111-1111-4111-8111-111111111111";

    private readonly TempDirectory _root = new();
    private readonly string _archive;

    public ArchivedChatReaderTests()
    {
        _archive = Directory.CreateDirectory(_root.Combine("archive")).FullName;
    }

    public void Dispose() => _root.Dispose();

    // ---- Visual Studio ---------------------------------------------------------------

    [Fact]
    public void VisualStudio_ReadsUserAndAssistantMessagesInOrder()
    {
        WriteEvents(
            VsEvent("session.start", new { }),
            VsEvent("user.message", new { content = "first question" }, "2026-09-29T09:00:00.000Z"),
            VsEvent("assistant.message", new { content = "first answer" }, "2026-09-29T09:00:05.000Z"),
            VsEvent("user.message", new { content = "second question" }));

        var content = ReadVs();

        Assert.Equal(
            [(ChatRole.User, "first question"), (ChatRole.Assistant, "first answer"), (ChatRole.User, "second question")],
            content.Messages.Select(message => (message.Role, message.Text)));
        Assert.Equal(new DateTimeOffset(2026, 9, 29, 9, 0, 5, TimeSpan.Zero), content.Messages[1].At);
    }

    /// <summary>
    /// Most assistant events only request a tool and carry no text: 2,778 of 8,308 had any in
    /// a real transcript. They are not messages.
    /// </summary>
    [Fact]
    public void VisualStudio_SkipsAssistantEventsThatOnlyRequestATool()
    {
        WriteEvents(
            VsEvent("user.message", new { content = "do it" }),
            VsEvent("assistant.message", new { content = "", toolRequests = new[] { new { name = "edit" } } }),
            VsEvent("tool.execution_start", new { toolName = "edit" }),
            VsEvent("tool.execution_complete", new { toolName = "edit" }),
            VsEvent("assistant.message", new { content = "Done." }));

        Assert.Equal(["do it", "Done."], ReadVs().Messages.Select(message => message.Text));
    }

    /// <summary>Visual Studio prefixes a message with a block the user never typed.</summary>
    [Fact]
    public void VisualStudio_LeavesTheIdeContextBlockOutOfAUserMessage()
    {
        WriteEvents(VsEvent("user.message", new { content = "<ide_context>\r\nIDE: Visual Studio\r\n</ide_context>\r\nwhat changed?" }));

        Assert.Equal("what changed?", Assert.Single(ReadVs().Messages).Text);
    }

    /// <summary>
    /// A full transcript is never cut off, so a block that does not close is the user's own
    /// text; removing it would throw their words away.
    /// </summary>
    [Fact]
    public void VisualStudio_KeepsAUserMessageWhoseIdeContextBlockNeverCloses()
    {
        WriteEvents(VsEvent("user.message", new { content = "<ide_context> is a tag I am asking about" }));

        Assert.Equal("<ide_context> is a tag I am asking about", Assert.Single(ReadVs().Messages).Text);
    }

    /// <summary>
    /// The words "user.message" appear inside tool output too; only an event that really is
    /// one counts.
    /// </summary>
    [Fact]
    public void VisualStudio_IgnoresALineThatOnlyMentionsAMessageEvent()
    {
        WriteEvents(
            VsEvent("tool.execution_complete", new { content = "found the event type user.message in the log" }),
            VsEvent("hook.start", new { content = "assistant.message" }),
            VsEvent("user.message", new { content = "the real one" }));

        Assert.Equal("the real one", Assert.Single(ReadVs().Messages).Text);
    }

    [Fact]
    public void VisualStudio_SkipsTornAndBlankLinesAndKeepsTheRest()
    {
        File.WriteAllLines(
            Path.Combine(SessionFolder(), "events.jsonl"),
            [
                "",
                "{ \"type\":\"user.message\", \"data\": { \"content\": \"cut off",
                VsEvent("user.message", new { content = "survivor" })
            ]);

        Assert.Equal("survivor", Assert.Single(ReadVs().Messages).Text);
    }

    [Fact]
    public void VisualStudio_ReturnsNothingForAChatWithNoTranscript()
    {
        Directory.CreateDirectory(Path.Combine(_archive, SessionId));

        Assert.Null(new VisualStudioChatProvider(Path.GetTempPath()).ReadArchivedChat(_archive, SessionId));
    }

    /// <summary>The id comes from the UI; it must never reach outside the project's folder.</summary>
    [Theory]
    [InlineData("..")]
    [InlineData("../elsewhere")]
    [InlineData("..\\elsewhere")]
    public void VisualStudio_RefusesAChatIdThatWouldEscapeTheProjectFolder(string chatId)
    {
        Assert.ThrowsAny<ArgumentException>(
            () => new VisualStudioChatProvider(Path.GetTempPath()).ReadArchivedChat(_archive, chatId));
    }

    // ---- Claude Code -----------------------------------------------------------------

    [Fact]
    public void Claude_ReadsTextFromPlainAndBlockContent()
    {
        WriteTranscript(
            ClaudeLine("user", "plain question", "2026-09-29T09:00:00.000Z"),
            ClaudeLine("assistant", new object[] { new { type = "text", text = "block answer" } }, "2026-09-29T09:00:05.000Z"));

        var content = ReadClaude();

        Assert.Equal(
            [(ChatRole.User, "plain question"), (ChatRole.Assistant, "block answer")],
            content.Messages.Select(message => (message.Role, message.Text)));
        Assert.Equal(new DateTimeOffset(2026, 9, 29, 9, 0, 5, TimeSpan.Zero), content.Messages[1].At);
    }

    /// <summary>Tool calls, results and thinking are most of a transcript and not conversation.</summary>
    [Fact]
    public void Claude_LeavesOutToolCallsResultsAndThinking()
    {
        WriteTranscript(
            ClaudeLine("user", "list the files"),
            ClaudeLine("assistant", new object[]
            {
                new { type = "thinking", thinking = "private reasoning" },
                new { type = "tool_use", id = "t1", name = "Bash", input = new { command = "ls" } }
            }),
            ClaudeLine("user", new object[] { new { type = "tool_result", tool_use_id = "t1", content = "a.txt" } }),
            ClaudeLine("assistant", new object[] { new { type = "text", text = "There is one file." } }));

        Assert.Equal(["list the files", "There is one file."], ReadClaude().Messages.Select(message => message.Text));
    }

    [Fact]
    public void Claude_JoinsTheTextBlocksOfOneRecordIntoOneMessage()
    {
        WriteTranscript(ClaudeLine("assistant", new object[]
        {
            new { type = "text", text = "First part." },
            new { type = "tool_use", id = "t1", name = "Read", input = new { } },
            new { type = "text", text = "Second part." }
        }));

        var text = Assert.Single(ReadClaude().Messages).Text;

        Assert.Contains("First part.", text);
        Assert.Contains("Second part.", text);
    }

    /// <summary>
    /// Claude Code wraps the user's message in reminders and IDE state and records slash
    /// commands as user messages of their own; none of it was typed.
    /// </summary>
    [Fact]
    public void Claude_LeavesOutTextClaudeCodeInjectedIntoAUserTurn()
    {
        WriteTranscript(
            ClaudeLine("user", "<command-name>/model</command-name>"),
            ClaudeLine("user", new object[]
            {
                new { type = "text", text = "<system-reminder>project rules</system-reminder>" },
                new { type = "text", text = "<ide_opened_file>a.cs</ide_opened_file>" },
                new { type = "text", text = "the real question" }
            }));

        Assert.Equal("the real question", Assert.Single(ReadClaude().Messages).Text);
    }

    /// <summary>An assistant that writes a tag is just writing: only a user turn is filtered.</summary>
    [Fact]
    public void Claude_KeepsAnAssistantMessageThatStartsLikeInjectedText()
    {
        WriteTranscript(ClaudeLine("assistant", "<system-reminder> is how Claude Code marks its own notes."));

        Assert.StartsWith("<system-reminder>", Assert.Single(ReadClaude().Messages).Text);
    }

    [Fact]
    public void Claude_SkipsMetaAndSubagentRecords()
    {
        WriteTranscript(
            ClaudeLine("user", "Continue from where you left off.", meta: true),
            ClaudeLine("assistant", "from a subagent", sidechain: true),
            ClaudeLine("user", "kept"));

        Assert.Equal("kept", Assert.Single(ReadClaude().Messages).Text);
    }

    /// <summary>
    /// An archived transcript stores the project root as a portable token; left alone it
    /// would sit in the middle of a path in the text the user reads.
    /// </summary>
    [Fact]
    public void Claude_ShowsThisPcsFolderInPlaceOfThePortableToken()
    {
        var token = ClaudeTranscriptPathMapper.ProjectRootToken;
        WriteTranscript(ClaudeLine("assistant", $"I edited {token}\\src\\Program.cs"));

        var content = ReadClaude(projectRoot: "C:\\work\\repo");

        Assert.Equal("I edited C:\\work\\repo\\src\\Program.cs", Assert.Single(content.Messages).Text);
    }

    [Fact]
    public void Claude_ShowsAPlaceholderForTheTokenWhenThePcsFolderIsUnknown()
    {
        WriteTranscript(ClaudeLine("assistant", $"I edited {ClaudeTranscriptPathMapper.ProjectRootToken}\\src\\Program.cs"));

        Assert.Equal("I edited <project>\\src\\Program.cs", Assert.Single(ReadClaude().Messages).Text);
    }

    [Fact]
    public void Claude_FindsATranscriptArchivedUnderASubfolder()
    {
        var path = Path.Combine(_archive, "src", "tools", $"{SessionId}.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, [ClaudeLine("user", "from a subfolder")]);

        Assert.Equal("from a subfolder", Assert.Single(ReadClaude().Messages).Text);
    }

    [Fact]
    public void Claude_ReturnsNothingForAChatThatIsNotInTheArchive()
    {
        Assert.Null(Claude().ReadArchivedChat(_archive, SessionId));
    }

    /// <summary>The id comes from the UI; only a well-formed session id is ever searched for.</summary>
    [Theory]
    [InlineData("..")]
    [InlineData("../elsewhere")]
    [InlineData("*")]
    [InlineData("not-a-guid")]
    public void Claude_NeverSearchesForAnIdThatIsNotASessionId(string chatId)
    {
        File.WriteAllLines(Path.Combine(_archive, $"{SessionId}.jsonl"), [ClaudeLine("user", "private")]);

        Assert.Null(Claude().ReadArchivedChat(_archive, chatId));
    }

    // ---- helpers ---------------------------------------------------------------------

    private string SessionFolder() => Directory.CreateDirectory(Path.Combine(_archive, SessionId)).FullName;

    private void WriteEvents(params string[] lines) =>
        File.WriteAllLines(Path.Combine(SessionFolder(), "events.jsonl"), lines);

    private ArchivedChatContent ReadVs() =>
        new VisualStudioChatProvider(Path.GetTempPath()).ReadArchivedChat(_archive, SessionId)
        ?? throw new InvalidOperationException("The chat was not found.");

    private void WriteTranscript(params string[] lines) =>
        File.WriteAllLines(Path.Combine(_archive, $"{SessionId}.jsonl"), lines);

    private ArchivedChatContent ReadClaude(string? projectRoot = null) =>
        Claude().ReadArchivedChat(_archive, SessionId, projectRoot)
        ?? throw new InvalidOperationException("The chat was not found.");

    private static ClaudeCodeChatProvider Claude() => new(new FakeProcessGuard());

    private static string VsEvent(string type, object data, string? timestamp = null) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = type,
            ["data"] = data,
            ["timestamp"] = timestamp
        });

    private static string ClaudeLine(
        string type,
        object content,
        string? timestamp = null,
        bool meta = false,
        bool sidechain = false)
    {
        var record = new Dictionary<string, object?>
        {
            ["type"] = type,
            ["isSidechain"] = sidechain,
            ["message"] = new { role = type, content },
            ["timestamp"] = timestamp
        };

        if (meta)
        {
            record["isMeta"] = true;
        }

        return JsonSerializer.Serialize(record);
    }
}
