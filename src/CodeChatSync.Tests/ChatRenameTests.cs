using System.Text;
using CodeChatSync.Core;
using CodeChatSync.Providers.Claude;
using CodeChatSync.Providers.VisualStudio;
using CodeChatSync.Tests.TestSupport;

namespace CodeChatSync.Tests;

/// <summary>
/// Renaming an archived chat: what is typed is normalized, each provider records the new
/// title the way its own tool does, and a rename never loses the file it rewrites.
/// </summary>
public sealed class ChatRenameTests : IDisposable
{
    private const string SessionA = "11111111-1111-4111-8111-111111111111";

    private readonly TempDirectory _root = new();
    private readonly string _archive;
    private readonly string _backups;

    public ChatRenameTests()
    {
        _archive = Directory.CreateDirectory(_root.Combine("archive")).FullName;
        _backups = _root.Combine("backups");
    }

    public void Dispose() => _root.Dispose();

    // ---- normalizing what was typed ---------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \r\n\t ")]
    public void Normalize_RejectsAnEmptyTitle(string? typed) => Assert.Null(ChatTitle.Normalize(typed));

    [Fact]
    public void Normalize_MakesOneLineOfIt() =>
        Assert.Equal("one two three", ChatTitle.Normalize("  one\r\ntwo \t three\n"));

    [Fact]
    public void Normalize_DropsControlCharacters() =>
        Assert.Equal("ab", ChatTitle.Normalize("a\u0000\u0007b"));

    [Fact]
    public void Normalize_CutsALongTitleWithoutSplittingASurrogatePair()
    {
        var typed = new string('x', ChatTitle.MaximumLength - 1) + "\U0001F600tail";

        var title = ChatTitle.Normalize(typed)!;

        Assert.Equal(ChatTitle.MaximumLength - 1, title.Length);
        Assert.False(char.IsHighSurrogate(title[^1]));
    }

    // ---- Visual Studio -----------------------------------------------------------------

    [Fact]
    public void VisualStudio_RewritesTheNameAndMarksItUserNamed()
    {
        WriteDescriptor("id: " + SessionA, "cwd: C:\\work", "name: \"old\"", "user_named: false", "updated_at: 2026-09-29T09:00:00.000Z");

        Assert.True(VisualStudio().RenameArchivedChat(_archive, SessionA, "New \"quoted\" name"));

        var text = File.ReadAllText(DescriptorPath);
        Assert.Contains("name: \"New \\\"quoted\\\" name\"", text);
        Assert.Contains("user_named: true", text);
        Assert.DoesNotContain("user_named: false", text);
        Assert.Contains("cwd: C:\\work", text);
        Assert.Contains("updated_at: 2026-09-29T09:00:00.000Z", text);

        var chat = Assert.Single(VisualStudio().ListArchivedChats(_archive));
        Assert.Equal("New \"quoted\" name", chat.Title);
    }

    [Fact]
    public void VisualStudio_AddsTheKeysADescriptorLacks()
    {
        WriteDescriptor("id: " + SessionA);

        Assert.True(VisualStudio().RenameArchivedChat(_archive, SessionA, "Fresh"));

        var text = File.ReadAllText(DescriptorPath);
        Assert.Contains("name: \"Fresh\"", text);
        Assert.Contains("user_named: true", text);
    }

    [Fact]
    public void VisualStudio_KeepsTheLineEndingsOfTheDescriptor()
    {
        File.WriteAllText(
            DescriptorPathEnsured(),
            "id: " + SessionA + "\r\nname: old\r\n",
            new UTF8Encoding(false));

        Assert.True(VisualStudio().RenameArchivedChat(_archive, SessionA, "Fresh"));

        var text = File.ReadAllText(DescriptorPath);
        Assert.DoesNotContain("\n", text.Replace("\r\n", string.Empty));
    }

    [Fact]
    public void VisualStudio_BacksTheDescriptorUpBeforeRewritingIt()
    {
        WriteDescriptor("id: " + SessionA, "name: original");

        Assert.True(VisualStudio().RenameArchivedChat(_archive, SessionA, "Fresh"));

        var backup = Assert.Single(Directory.GetFiles(_backups, "workspace.yaml", SearchOption.AllDirectories));
        Assert.Contains("name: original", File.ReadAllText(backup));
    }

    [Fact]
    public void VisualStudio_ReportsAChatThatIsNotThere() =>
        Assert.False(VisualStudio().RenameArchivedChat(_archive, SessionA, "Fresh"));

    [Fact]
    public void VisualStudio_NeverReachesOutsideTheProjectFolder() =>
        Assert.ThrowsAny<Exception>(() => VisualStudio().RenameArchivedChat(_archive, "..", "Fresh"));

    // ---- Claude Code -------------------------------------------------------------------

    [Fact]
    public void Claude_AppendsACustomTitleThatWinsOverTheGeneratedOne()
    {
        var path = WriteTranscript(
            "src",
            """{"type":"ai-title","aiTitle":"Generated","sessionId":"x"}""",
            """{"type":"user","timestamp":"2026-09-29T09:00:00.000Z"}""");

        Assert.True(Claude().RenameArchivedChat(_archive, SessionA, "Mine"));

        Assert.Equal("Mine", Assert.Single(Claude().ListArchivedChats(_archive)).Title);
        var last = File.ReadAllLines(path)[^1];
        Assert.Contains("\"type\":\"custom-title\"", last);
        Assert.Contains($"\"sessionId\":\"{SessionA}\"", last);
    }

    [Fact]
    public void Claude_TheLatestRenameWins()
    {
        WriteTranscript(string.Empty, """{"type":"user","timestamp":"2026-09-29T09:00:00.000Z"}""");

        Assert.True(Claude().RenameArchivedChat(_archive, SessionA, "First"));
        Assert.True(Claude().RenameArchivedChat(_archive, SessionA, "Second"));

        Assert.Equal("Second", Assert.Single(Claude().ListArchivedChats(_archive)).Title);
    }

    [Fact]
    public void Claude_DoesNotMergeTheRecordIntoATornLastLine()
    {
        var path = Path.Combine(_archive, $"{SessionA}.jsonl");
        File.WriteAllText(path, "{\"type\":\"user\",\"timestamp\":\"2026-09-29T09:00:00.000Z\"}\n{\"type\":\"as");

        Assert.True(Claude().RenameArchivedChat(_archive, SessionA, "Mine"));

        Assert.Equal("Mine", Assert.Single(Claude().ListArchivedChats(_archive)).Title);
    }

    [Fact]
    public void Claude_ReportsAChatThatIsNotThere() =>
        Assert.False(Claude().RenameArchivedChat(_archive, SessionA, "Mine"));

    [Fact]
    public void Claude_OnlySearchesForAWellFormedId() =>
        Assert.False(Claude().RenameArchivedChat(_archive, "..\\..\\x", "Mine"));

    // ---- ChatLibrary.Rename ---------------------------------------------------------------

    [Fact]
    public void Rename_PullsEditsThenPublishes()
    {
        var syncRoot = _root.Combine("sync");
        var project = ProjectIdentity.FromRemote("https://github.com/acme/widgets.git");
        var projectFolder = Directory.CreateDirectory(Path.Combine(syncRoot, "claudecode", project.Slug)).FullName;
        File.WriteAllText(
            Path.Combine(projectFolder, $"{SessionA}.jsonl"),
            """{"type":"user","timestamp":"2026-09-29T09:00:00.000Z"}""" + "\n");
        var publisher = new RecordingPublisher();

        var result = ChatLibrary.Rename(
            new SharedConfig(), syncRoot, [Claude()], publisher, "claudecode", project, SessionA, "  New   title ");

        Assert.True(result.Succeeded);
        Assert.Equal(["prepare", "publish"], publisher.Calls);
        Assert.Equal("New title", Assert.Single(Claude().ListArchivedChats(projectFolder)).Title);
    }

    [Fact]
    public void Rename_DoesNotTouchAnythingWhenThePullFails()
    {
        var syncRoot = _root.Combine("sync");
        var project = ProjectIdentity.FromRemote("https://github.com/acme/widgets.git");
        var projectFolder = Directory.CreateDirectory(Path.Combine(syncRoot, "claudecode", project.Slug)).FullName;
        var transcript = Path.Combine(projectFolder, $"{SessionA}.jsonl");
        File.WriteAllText(transcript, """{"type":"user"}""" + "\n");
        var publisher = new RecordingPublisher { PrepareResult = SyncPublishResult.Stop("offline") };

        var result = ChatLibrary.Rename(
            new SharedConfig(), syncRoot, [Claude()], publisher, "claudecode", project, SessionA, "Title");

        Assert.False(result.Succeeded);
        Assert.Equal(["prepare"], publisher.Calls);
        Assert.Equal("""{"type":"user"}""" + "\n", File.ReadAllText(transcript));
    }

    [Fact]
    public void Rename_RefusesAnEmptyTitle()
    {
        var project = ProjectIdentity.FromRemote("https://github.com/acme/widgets.git");

        var result = ChatLibrary.Rename(
            new SharedConfig(), _root.Combine("sync"), [Claude()], null, "claudecode", project, SessionA, "  ");

        Assert.False(result.Succeeded);
    }

    // ---- deleting ---------------------------------------------------------------------------

    [Fact]
    public void VisualStudio_DeleteRemovesTheWholeSessionFolderAndNothingElse()
    {
        WriteDescriptor("id: " + SessionA);
        File.WriteAllText(Path.Combine(_archive, SessionA, "events.jsonl"), "{}");
        var other = Directory.CreateDirectory(Path.Combine(_archive, "other-session")).FullName;
        File.WriteAllText(Path.Combine(other, "workspace.yaml"), "id: other-session");

        Assert.True(VisualStudio().DeleteArchivedChat(_archive, SessionA));

        Assert.False(Directory.Exists(Path.Combine(_archive, SessionA)));
        Assert.True(File.Exists(Path.Combine(other, "workspace.yaml")));
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    public void VisualStudio_DeleteRefusesAnythingButOneFolderName(string chatId)
    {
        WriteDescriptor("id: " + SessionA);

        Assert.False(VisualStudio().DeleteArchivedChat(_archive, chatId));

        Assert.True(File.Exists(DescriptorPath));
    }

    [Fact]
    public void VisualStudio_DeleteReportsAChatThatIsNotThere() =>
        Assert.False(VisualStudio().DeleteArchivedChat(_archive, SessionA));

    [Fact]
    public void Claude_DeleteRemovesOnlyThatTranscript()
    {
        var path = WriteTranscript("src", """{"type":"user"}""");
        var other = Path.Combine(_archive, "src", "22222222-2222-4222-8222-222222222222.jsonl");
        File.WriteAllText(other, """{"type":"user"}""");

        Assert.True(Claude().DeleteArchivedChat(_archive, SessionA));

        Assert.False(File.Exists(path));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public void Claude_DeleteReportsAChatThatIsNotThere() =>
        Assert.False(Claude().DeleteArchivedChat(_archive, SessionA));

    [Fact]
    public void Delete_PullsRemovesThenPublishes()
    {
        var syncRoot = _root.Combine("sync");
        var project = ProjectIdentity.FromRemote("https://github.com/acme/widgets.git");
        var projectFolder = Directory.CreateDirectory(Path.Combine(syncRoot, "claudecode", project.Slug)).FullName;
        var transcript = Path.Combine(projectFolder, $"{SessionA}.jsonl");
        File.WriteAllText(transcript, """{"type":"user"}""" + "\n");
        var publisher = new RecordingPublisher();

        var result = ChatLibrary.Delete(
            new SharedConfig(), syncRoot, [Claude()], publisher, "claudecode", project, SessionA);

        Assert.True(result.Succeeded);
        Assert.Equal(["prepare", "publish"], publisher.Calls);
        Assert.False(File.Exists(transcript));
    }

    [Fact]
    public void Delete_KeepsTheChatWhenThePullFails()
    {
        var syncRoot = _root.Combine("sync");
        var project = ProjectIdentity.FromRemote("https://github.com/acme/widgets.git");
        var projectFolder = Directory.CreateDirectory(Path.Combine(syncRoot, "claudecode", project.Slug)).FullName;
        var transcript = Path.Combine(projectFolder, $"{SessionA}.jsonl");
        File.WriteAllText(transcript, """{"type":"user"}""" + "\n");
        var publisher = new RecordingPublisher { PrepareResult = SyncPublishResult.Stop("offline") };

        var result = ChatLibrary.Delete(
            new SharedConfig(), syncRoot, [Claude()], publisher, "claudecode", project, SessionA);

        Assert.False(result.Succeeded);
        Assert.True(File.Exists(transcript));
    }

    // ---- helpers ----------------------------------------------------------------------------

    private string DescriptorPath => Path.Combine(_archive, SessionA, "workspace.yaml");

    private string DescriptorPathEnsured()
    {
        Directory.CreateDirectory(Path.Combine(_archive, SessionA));
        return DescriptorPath;
    }

    private void WriteDescriptor(params string[] lines) =>
        File.WriteAllLines(DescriptorPathEnsured(), lines, new UTF8Encoding(false));

    private string WriteTranscript(string subfolder, params string[] lines)
    {
        var folder = Directory.CreateDirectory(Path.Combine(_archive, subfolder)).FullName;
        var path = Path.Combine(folder, $"{SessionA}.jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }

    private VisualStudioChatProvider VisualStudio() =>
        new(sessionStateRoot: Path.GetTempPath(), titleBackupDirectory: _backups);

    private static ClaudeCodeChatProvider Claude() => new(new FakeProcessGuard());

    private sealed class RecordingPublisher : ISyncPublisher
    {
        public List<string> Calls { get; } = [];

        public SyncPublishResult PrepareResult { get; init; } = SyncPublishResult.Ok();

        public SyncPublishResult PrepareForSync()
        {
            Calls.Add("prepare");
            return PrepareResult;
        }

        public SyncPublishResult PublishChanges(int pushedCount)
        {
            Calls.Add("publish");
            return SyncPublishResult.Ok();
        }
    }
}
