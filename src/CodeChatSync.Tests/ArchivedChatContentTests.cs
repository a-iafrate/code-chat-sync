using CodeChatSync.Core;
using CodeChatSync.Tests.TestSupport;

namespace CodeChatSync.Tests;

/// <summary>
/// The viewer shows tens of megabytes of transcript as a list of messages, so the limits that
/// keep it responsive — and the guarantee that a chat can only be read from inside its own
/// project's folder — are part of the contract.
/// </summary>
public sealed class ArchivedChatContentTests : IDisposable
{
    private const string Remote = "https://github.com/example/viewer.git";

    private readonly TempDirectory _root = new();
    private readonly string _syncRoot;

    public ArchivedChatContentTests()
    {
        _syncRoot = Directory.CreateDirectory(_root.Combine("sync")).FullName;
    }

    public void Dispose() => _root.Dispose();

    // ---- ArchivedChatContentBuilder --------------------------------------------------

    [Fact]
    public void Builder_KeepsMessagesInTheOrderTheyWereAdded()
    {
        var builder = new ArchivedChatContentBuilder();
        var at = new DateTimeOffset(2026, 9, 29, 9, 0, 0, TimeSpan.Zero);

        builder.Add(ChatRole.User, "first", at);
        builder.Add(ChatRole.Assistant, "second", null);

        var content = builder.Build();

        Assert.Equal([ChatRole.User, ChatRole.Assistant], content.Messages.Select(message => message.Role));
        Assert.Equal(["first", "second"], content.Messages.Select(message => message.Text));
        Assert.Equal(at, content.Messages[0].At);
        Assert.False(content.IsTruncated);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \r\n  ")]
    public void Builder_IgnoresBlankMessages(string? text)
    {
        var builder = new ArchivedChatContentBuilder();

        builder.Add(ChatRole.Assistant, text, null);

        Assert.Empty(builder.Build().Messages);
    }

    [Fact]
    public void Builder_TrimsTheWhitespaceAroundAMessage()
    {
        var builder = new ArchivedChatContentBuilder();

        builder.Add(ChatRole.User, "\r\n  hello  \r\n", null);

        Assert.Equal("hello", Assert.Single(builder.Build().Messages).Text);
    }

    [Fact]
    public void Builder_CutsAMessageThatIsTooLongAndSaysSo()
    {
        var builder = new ArchivedChatContentBuilder();

        builder.Add(ChatRole.Assistant, new string('x', ArchivedChatContentBuilder.MaximumMessageLength + 500), null);

        var content = builder.Build();
        var text = Assert.Single(content.Messages).Text;
        Assert.StartsWith(new string('x', ArchivedChatContentBuilder.MaximumMessageLength), text);
        Assert.Contains("500 more characters not shown", text);
        Assert.Equal(1, content.ClippedMessages);
        Assert.True(content.IsTruncated);
    }

    [Fact]
    public void Builder_KeepsAMessageExactlyAtTheLimitWhole()
    {
        var builder = new ArchivedChatContentBuilder();

        builder.Add(ChatRole.Assistant, new string('x', ArchivedChatContentBuilder.MaximumMessageLength), null);

        var content = builder.Build();
        Assert.Equal(ArchivedChatContentBuilder.MaximumMessageLength, Assert.Single(content.Messages).Text.Length);
        Assert.False(content.IsTruncated);
    }

    /// <summary>Half of a surrogate pair is not a character, and renders as garbage.</summary>
    [Fact]
    public void Builder_NeverCutsBetweenTheHalvesOfASurrogatePair()
    {
        var builder = new ArchivedChatContentBuilder();
        var text = new string('x', ArchivedChatContentBuilder.MaximumMessageLength - 1) + "😀" + "tail";

        builder.Add(ChatRole.Assistant, text, null);

        var shown = Assert.Single(builder.Build().Messages).Text;
        var cut = shown[..shown.IndexOf(Environment.NewLine, StringComparison.Ordinal)];
        Assert.False(char.IsHighSurrogate(cut[^1]));
    }

    [Fact]
    public void Builder_CountsMessagesBeyondTheCapInsteadOfKeepingThem()
    {
        var builder = new ArchivedChatContentBuilder();

        for (var index = 0; index < ArchivedChatContentBuilder.MaximumMessages + 7; index++)
        {
            builder.Add(ChatRole.User, $"message {index}", null);
        }

        var content = builder.Build();
        Assert.Equal(ArchivedChatContentBuilder.MaximumMessages, content.Messages.Count);
        Assert.Equal(7, content.OmittedMessages);
        Assert.True(content.IsTruncated);
        Assert.Equal("message 0", content.Messages[0].Text);
    }

    // ---- ChatLibrary.Read ------------------------------------------------------------

    [Fact]
    public void Read_AsksTheProviderForTheChatInsideTheProjectsFolder()
    {
        var reader = new FakeReader("fake") { Content = Content("hello") };
        var config = Config("fake", ProjectPath);

        var content = ChatLibrary.Read(config, new SharedConfig(), _syncRoot, [reader], "fake", Identity(), "chat-1");

        Assert.Equal("hello", Assert.Single(Assert.IsType<ArchivedChatContent>(content).Messages).Text);
        Assert.Equal(Path.Combine(_syncRoot, "fake", Identity().Slug), reader.RequestedFolder);
        Assert.Equal("chat-1", reader.RequestedChat);
    }

    /// <summary>
    /// The archive stores a project's root in a portable form; showing it as a real path
    /// needs this PC's folder for the project.
    /// </summary>
    [Fact]
    public void Read_PassesThisPcsFolderForTheProject()
    {
        var reader = new FakeReader("fake") { Content = Content("hello") };

        ChatLibrary.Read(Config("fake", ProjectPath), new SharedConfig(), _syncRoot, [reader], "fake", Identity(), "chat-1");

        Assert.Equal(ProjectPath, reader.RequestedProjectRoot);
    }

    [Fact]
    public void Read_UsesTheSharedProjectName()
    {
        var shared = new SharedConfig();
        shared.Projects.Add(new SharedProjectEntry { Remote = Identity().NormalizedRemote, Name = "client-erp" });
        var reader = new FakeReader("fake") { Content = Content("hello") };

        ChatLibrary.Read(Config("fake", ProjectPath), shared, _syncRoot, [reader], "fake", Identity(), "chat-1");

        Assert.Equal(Path.Combine(_syncRoot, "fake", "client-erp"), reader.RequestedFolder);
    }

    [Fact]
    public void Read_ReturnsNothingForAProviderThatCannotReadAnArchive()
    {
        var content = ChatLibrary.Read(
            Config("plain", ProjectPath), new SharedConfig(), _syncRoot,
            [new FakeChatProvider(_root.Path) { Id = "plain" }], "plain", Identity(), "chat-1");

        Assert.Null(content);
    }

    [Fact]
    public void Read_ReturnsNothingWhenTheChatIsNotInTheArchive()
    {
        var reader = new FakeReader("fake") { Content = null };

        var content = ChatLibrary.Read(
            Config("fake", ProjectPath), new SharedConfig(), _syncRoot, [reader], "fake", Identity(), "gone");

        Assert.Null(content);
    }

    /// <summary>
    /// The shared mapping comes from the sync repository, so a hostile name must never
    /// point a read outside the project's own folder.
    /// </summary>
    [Theory]
    [InlineData("..")]
    [InlineData("../escape")]
    [InlineData("a\\b")]
    public void Read_RefusesAProjectNameThatWouldEscapeItsFolder(string name)
    {
        var shared = new SharedConfig();
        shared.Projects.Add(new SharedProjectEntry { Remote = Identity().NormalizedRemote, Name = name });
        var reader = new FakeReader("fake") { Content = Content("secret") };

        var content = ChatLibrary.Read(
            Config("fake", ProjectPath), shared, _syncRoot, [reader], "fake", Identity(), "chat-1");

        Assert.Null(content);
        Assert.Null(reader.RequestedFolder);
    }

    // ---- helpers ---------------------------------------------------------------------

    private string ProjectPath => Path.GetFullPath(_root.Combine("repo"));

    private static ProjectIdentity Identity() => ProjectIdentity.FromRemote(Remote);

    private LocalConfig Config(string providerId, string localPath)
    {
        var config = new LocalConfig { SyncRootPath = _syncRoot };
        config.AddOrUpdate(Identity(), localPath, providerId);
        return config;
    }

    private static ArchivedChatContent Content(string text) =>
        new([new ChatMessage(ChatRole.User, text, null)], 0, 0);

    private sealed class FakeReader(string id) : IArchivedChatReader
    {
        public string Id { get; } = id;

        public IReadOnlyList<string> ProcessNames { get; } = [];

        public ArchivedChatContent? Content { get; init; }

        public string? RequestedFolder { get; private set; }

        public string? RequestedChat { get; private set; }

        public string? RequestedProjectRoot { get; private set; }

        public ArchivedChatContent? ReadArchivedChat(string projectSyncFolder, string chatId, string? projectRoot = null)
        {
            RequestedFolder = projectSyncFolder;
            RequestedChat = chatId;
            RequestedProjectRoot = projectRoot;
            return Content;
        }

        public IEnumerable<ChatLocation> Discover(ProjectInfo project) => [];

        public string MapToLocal(ProjectInfo project, string relativePath) => throw new NotSupportedException();
    }
}
