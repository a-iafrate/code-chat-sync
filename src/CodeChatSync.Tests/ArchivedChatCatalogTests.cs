using System.Text;
using CodeChatSync.Providers.Claude;
using CodeChatSync.Providers.VisualStudio;
using CodeChatSync.Tests.TestSupport;

namespace CodeChatSync.Tests;

/// <summary>
/// What the Chat Library lists for each provider. Both read the archive in the sync folder,
/// where file timestamps are the moment of the last <c>git pull</c> rather than anything
/// to do with the conversation, so every time shown has to come from the archived content.
/// </summary>
public sealed class ArchivedChatCatalogTests : IDisposable
{
    private const string SessionA = "11111111-1111-4111-8111-111111111111";
    private const string SessionB = "22222222-2222-4222-8222-222222222222";

    private readonly TempDirectory _root = new();
    private readonly string _archive;

    public ArchivedChatCatalogTests()
    {
        _archive = Directory.CreateDirectory(_root.Combine("archive")).FullName;
    }

    public void Dispose() => _root.Dispose();

    // ---- Visual Studio --------------------------------------------------------------

    [Fact]
    public void VisualStudio_ReadsTheTitleAndTimesFromTheDescriptor()
    {
        WriteSession(SessionA, name: "name: Fix the current code", created: "2026-09-29T09:00:00.000Z", updated: "2026-09-29T09:30:00.000Z");

        var chat = Assert.Single(VisualStudio().ListArchivedChats(_archive));

        Assert.Equal("visualstudio", chat.ProviderId);
        Assert.Equal(SessionA, chat.Id);
        Assert.Equal("Fix the current code", chat.Title);
        Assert.Equal(new DateTimeOffset(2026, 9, 29, 9, 0, 0, TimeSpan.Zero), chat.CreatedAt);
        Assert.Equal(new DateTimeOffset(2026, 9, 29, 9, 30, 0, TimeSpan.Zero), chat.UpdatedAt);
    }

    /// <summary>
    /// Visual Studio stores the whole first message as the name, behind a block describing
    /// the open solution that the user never typed and the chat history does not show.
    /// </summary>
    [Fact]
    public void VisualStudio_LeavesTheIdeContextBlockOutOfTheTitle()
    {
        WriteSession(SessionA, name: "name: \"<ide_context>\\r\\nIDE: Visual Studio\\r\\n</ide_context>\\r\\nwhat   changed?\"");

        Assert.Equal("what changed?", Assert.Single(VisualStudio().ListArchivedChats(_archive)).Title);
    }

    /// <summary>
    /// A name cut off inside that block holds nothing the user wrote, so no title is
    /// better than one made of the preamble.
    /// </summary>
    [Fact]
    public void VisualStudio_HasNoTitleWhenTheNameWasCutOffInsideTheIdeContext()
    {
        WriteSession(SessionA, name: "name: \"<ide_context>\\r\\nIDE: Visual Studio Enterprise 2026\"");

        Assert.Null(Assert.Single(VisualStudio().ListArchivedChats(_archive)).Title);
    }

    [Fact]
    public void VisualStudio_ShortensAVeryLongTitleToOneLine()
    {
        WriteSession(SessionA, name: "name: " + new string('x', 400));

        var title = Assert.Single(VisualStudio().ListArchivedChats(_archive)).Title;

        Assert.NotNull(title);
        Assert.Equal(CopilotTitleLimit, title!.Length);
        Assert.EndsWith("…", title);
    }

    [Fact]
    public void VisualStudio_CountsEveryArchivedFileOfTheChat()
    {
        WriteSession(SessionA, name: "name: sized");
        File.WriteAllText(Path.Combine(_archive, SessionA, "events.jsonl"), new string('e', 1000));
        Directory.CreateDirectory(Path.Combine(_archive, SessionA, "checkpoints"));
        File.WriteAllText(Path.Combine(_archive, SessionA, "checkpoints", "index.md"), new string('c', 500));
        var descriptorLength = new FileInfo(Path.Combine(_archive, SessionA, "workspace.yaml")).Length;

        var chat = Assert.Single(VisualStudio().ListArchivedChats(_archive));

        Assert.Equal(3, chat.FileCount);
        Assert.Equal(descriptorLength + 1000 + 500, chat.SizeBytes);
    }

    [Fact]
    public void VisualStudio_IgnoresAFolderWithNoDescriptor()
    {
        Directory.CreateDirectory(Path.Combine(_archive, SessionB));
        File.WriteAllText(Path.Combine(_archive, SessionB, "events.jsonl"), "{}");

        Assert.Empty(VisualStudio().ListArchivedChats(_archive));
    }

    [Fact]
    public void VisualStudio_ReturnsNothingForAFolderThatDoesNotExist()
    {
        Assert.Empty(VisualStudio().ListArchivedChats(Path.Combine(_root.Path, "absent")));
    }

    // ---- Claude Code ----------------------------------------------------------------

    /// <summary>
    /// Claude transcripts are archived under the folder the session was started in, so a
    /// listing of the top level alone silently hid every chat started in a subfolder.
    /// </summary>
    [Fact]
    public void Claude_FindsTranscriptsAtTheRootAndInSubfolders()
    {
        WriteTranscript(Path.Combine(_archive, $"{SessionA}.jsonl"), Line("2026-09-29T09:00:00.000Z"));
        WriteTranscript(Path.Combine(_archive, "src", $"{SessionB}.jsonl"), Line("2026-09-30T09:00:00.000Z"));

        var chats = Claude().ListArchivedChats(_archive);

        Assert.Equal([SessionA, SessionB], chats.Select(chat => chat.Id).Order());
    }

    [Fact]
    public void Claude_FindsATranscriptNestedSeveralFoldersDeep()
    {
        WriteTranscript(Path.Combine(_archive, "src", "tools", "build", $"{SessionA}.jsonl"), Line("2026-09-29T09:00:00.000Z"));

        Assert.Equal(SessionA, Assert.Single(Claude().ListArchivedChats(_archive)).Id);
    }

    [Fact]
    public void Claude_TakesTheTimesFromInsideTheTranscript()
    {
        var path = Path.Combine(_archive, $"{SessionA}.jsonl");

        // Out of order on purpose: bookkeeping records are not written in time order.
        WriteTranscript(
            path,
            Line("2026-09-29T10:00:00.000Z"),
            Line("2026-09-29T09:00:00.000Z"),
            Line("2026-09-29T11:00:00.000Z"));

        // As after a pull: the file looks as if it was written long after the conversation.
        File.SetLastWriteTimeUtc(path, new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var chat = Assert.Single(Claude().ListArchivedChats(_archive));

        Assert.Equal(new DateTimeOffset(2026, 9, 29, 9, 0, 0, TimeSpan.Zero), chat.CreatedAt);
        Assert.Equal(new DateTimeOffset(2026, 9, 29, 11, 0, 0, TimeSpan.Zero), chat.UpdatedAt);
    }

    [Fact]
    public void Claude_FallsBackToTheFileTimeWhenTheTranscriptHasNone()
    {
        var path = Path.Combine(_archive, $"{SessionA}.jsonl");
        WriteTranscript(path, "{\"type\":\"summary\"}");
        var written = new DateTime(2026, 8, 15, 8, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, written);

        var chat = Assert.Single(Claude().ListArchivedChats(_archive));

        Assert.Null(chat.CreatedAt);
        Assert.Equal(new DateTimeOffset(written, TimeSpan.Zero), chat.UpdatedAt);
    }

    [Fact]
    public void Claude_UsesTheGeneratedTitleAndTheFileSize()
    {
        var path = Path.Combine(_archive, $"{SessionA}.jsonl");
        WriteTranscript(path, "{\"aiTitle\":\"first title\"}", "{\"aiTitle\":\"Renamed chat\"}", Line("2026-09-29T09:00:00.000Z"));

        var chat = Assert.Single(Claude().ListArchivedChats(_archive));

        Assert.Equal("claudecode", chat.ProviderId);
        Assert.Equal("Renamed chat", chat.Title);
        Assert.Equal(1, chat.FileCount);
        Assert.Equal(new FileInfo(path).Length, chat.SizeBytes);
    }

    [Fact]
    public void Claude_IgnoresFilesThatAreNotSessionTranscripts()
    {
        WriteTranscript(Path.Combine(_archive, "notes.jsonl"), Line("2026-09-29T09:00:00.000Z"));
        WriteTranscript(Path.Combine(_archive, "agent-abc.jsonl"), Line("2026-09-29T09:00:00.000Z"));
        File.WriteAllText(Path.Combine(_archive, $"{SessionA}.txt"), "not a transcript");

        Assert.Empty(Claude().ListArchivedChats(_archive));
    }

    [Fact]
    public void Claude_ListsTheMostRecentlyActiveChatFirst()
    {
        WriteTranscript(Path.Combine(_archive, $"{SessionA}.jsonl"), Line("2026-09-01T09:00:00.000Z"));
        WriteTranscript(Path.Combine(_archive, $"{SessionB}.jsonl"), Line("2026-09-30T09:00:00.000Z"));

        Assert.Equal([SessionB, SessionA], Claude().ListArchivedChats(_archive).Select(chat => chat.Id));
    }

    [Fact]
    public void Claude_ReturnsNothingForAFolderThatDoesNotExist()
    {
        Assert.Empty(Claude().ListArchivedChats(Path.Combine(_root.Path, "absent")));
    }

    [Fact]
    public void Claude_RefusesAnArchiveFolderThatIsALink()
    {
        var target = Directory.CreateDirectory(_root.Combine("elsewhere")).FullName;
        var link = _root.Combine("linked");
        if (!ClaudeProjectsFixture.TryCreateDirectoryLink(link, target))
        {
            return;
        }

        Assert.Throws<InvalidOperationException>(() => Claude().ListArchivedChats(link));
    }

    // ---- Claude Code: titles --------------------------------------------------------

    /// <summary>
    /// Many sessions never get a generated title, and an archive where most chats have no
    /// name cannot be browsed — what the user first typed is the next best thing.
    /// </summary>
    [Fact]
    public void Claude_FallsBackToTheFirstPromptWhenThereIsNoGeneratedTitle()
    {
        WriteTranscript(
            Path.Combine(_archive, $"{SessionA}.jsonl"),
            UserLine(new object[] { new { type = "text", text = "How do I restore a chat?" } }),
            Line("2026-09-29T09:00:00.000Z"));

        Assert.Equal("How do I restore a chat?", Assert.Single(Claude().ListArchivedChats(_archive)).Title);
    }

    [Fact]
    public void Claude_PrefersTheGeneratedTitleOverTheFirstPrompt()
    {
        WriteTranscript(
            Path.Combine(_archive, $"{SessionA}.jsonl"),
            UserLine("what the user typed"),
            "{\"aiTitle\":\"Generated title\"}");

        Assert.Equal("Generated title", Assert.Single(Claude().ListArchivedChats(_archive)).Title);
    }

    [Fact]
    public void Claude_ReadsAPromptStoredAsPlainText()
    {
        WriteTranscript(Path.Combine(_archive, $"{SessionA}.jsonl"), UserLine("plain string prompt"));

        Assert.Equal("plain string prompt", Assert.Single(Claude().ListArchivedChats(_archive)).Title);
    }

    /// <summary>
    /// Claude Code wraps what the user typed in reminders and IDE state, and records local
    /// slash commands as user messages of their own. None of that is a prompt.
    /// </summary>
    [Fact]
    public void Claude_SkipsInjectedTextWhenFindingTheFirstPrompt()
    {
        WriteTranscript(
            Path.Combine(_archive, $"{SessionA}.jsonl"),
            UserLine("<command-name>/model</command-name>"),
            UserLine("Caveat: The messages below were generated by the user while running local commands."),
            UserLine(new object[]
            {
                new { type = "text", text = "<system-reminder>project rules</system-reminder>" },
                new { type = "text", text = "<ide_opened_file>file.cs</ide_opened_file>" },
                new { type = "text", text = "the real question" }
            }));

        Assert.Equal("the real question", Assert.Single(Claude().ListArchivedChats(_archive)).Title);
    }

    [Fact]
    public void Claude_SkipsMetaToolResultAndSidechainLinesWhenFindingTheFirstPrompt()
    {
        WriteTranscript(
            Path.Combine(_archive, $"{SessionA}.jsonl"),
            UserLine("Continue from where you left off.", meta: true),
            UserLine(new object[] { new { type = "tool_result", content = "output" } }),
            UserLine("typed by a subagent", sidechain: true),
            UserLine("the first real prompt"));

        Assert.Equal("the first real prompt", Assert.Single(Claude().ListArchivedChats(_archive)).Title);
    }

    [Fact]
    public void Claude_ShowsAMultiLinePromptOnOneLineAndCutsItShort()
    {
        WriteTranscript(
            Path.Combine(_archive, $"{SessionA}.jsonl"),
            UserLine("first line\r\n\r\n  second   line " + new string('x', 400)));

        var title = Assert.Single(Claude().ListArchivedChats(_archive)).Title;

        Assert.NotNull(title);
        Assert.StartsWith("first line second line xxx", title);
        Assert.DoesNotContain('\n', title);
        Assert.Equal(200, title!.Length);
    }

    [Fact]
    public void Claude_HasNoTitleWhenNeitherATitleNorAPromptExists()
    {
        WriteTranscript(Path.Combine(_archive, $"{SessionA}.jsonl"), Line("2026-09-29T09:00:00.000Z"));

        Assert.Null(Assert.Single(Claude().ListArchivedChats(_archive)).Title);
    }

    // ---- helpers --------------------------------------------------------------------

    private const int CopilotTitleLimit = 120;

    private static VisualStudioChatProvider VisualStudio() => new(sessionStateRoot: Path.GetTempPath());

    private static ClaudeCodeChatProvider Claude() => new(new FakeProcessGuard());

    private void WriteSession(string id, string name, string? created = null, string? updated = null)
    {
        var folder = Directory.CreateDirectory(Path.Combine(_archive, id)).FullName;
        var lines = new List<string> { $"id: {id}", name };
        if (created is not null)
        {
            lines.Add($"created_at: {created}");
        }

        if (updated is not null)
        {
            lines.Add($"updated_at: {updated}");
        }

        File.WriteAllLines(Path.Combine(folder, "workspace.yaml"), lines, new UTF8Encoding(false));
    }

    private static string Line(string timestamp) => $"{{\"type\":\"user\",\"timestamp\":\"{timestamp}\"}}";

    /// <summary>A user record as Claude Code writes it, content being text or content blocks.</summary>
    private static string UserLine(object content, bool meta = false, bool sidechain = false)
    {
        var record = new Dictionary<string, object>
        {
            ["type"] = "user",
            ["isSidechain"] = sidechain,
            ["message"] = new { role = "user", content }
        };

        if (meta)
        {
            record["isMeta"] = true;
        }

        return System.Text.Json.JsonSerializer.Serialize(record);
    }

    private static void WriteTranscript(string path, params string[] lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, lines);
    }
}
