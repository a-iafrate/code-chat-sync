using CodeChatSync.Core;
using CodeChatSync.Tests.TestSupport;

namespace CodeChatSync.Tests;

/// <summary>
/// The Chat Library shows what is archived in the sync folder. It must work from the
/// registration alone — a project whose folder is gone from this PC still has chats worth
/// looking at — and must never turn an unusable registration into a path.
/// </summary>
public sealed class ChatLibraryTests : IDisposable
{
    private const string Remote = "https://github.com/example/library.git";

    private readonly TempDirectory _root = new();
    private readonly string _syncRoot;

    public ChatLibraryTests()
    {
        _syncRoot = Directory.CreateDirectory(_root.Combine("sync")).FullName;
    }

    public void Dispose() => _root.Dispose();

    [Fact]
    public void List_AsksTheProviderForTheProjectsFolderInsideTheSyncRoot()
    {
        var catalog = new FakeCatalog("fake");
        var config = Config(("fake", Remote));

        ChatLibrary.List(config, new SharedConfig(), _syncRoot, [catalog]);

        var requested = Assert.Single(catalog.RequestedFolders);
        Assert.Equal(Path.Combine(_syncRoot, "fake", ProjectIdentity.FromRemote(Remote).Slug), requested);
    }

    [Fact]
    public void List_UsesTheSharedProjectNameWhenThereIsOne()
    {
        var catalog = new FakeCatalog("fake");
        var shared = new SharedConfig();
        shared.Projects.Add(new SharedProjectEntry
        {
            Remote = ProjectIdentity.FromRemote(Remote).NormalizedRemote,
            Name = "client-erp"
        });

        var library = ChatLibrary.List(Config(("fake", Remote)), shared, _syncRoot, [catalog]);

        Assert.Equal("client-erp", Assert.Single(library).Name);
        Assert.Equal(Path.Combine(_syncRoot, "fake", "client-erp"), Assert.Single(catalog.RequestedFolders));
    }

    [Fact]
    public void List_PutsTheNewestChatFirstAndUndatedChatsLast()
    {
        var older = Chat("fake", "older", updated: At(2026, 9, 1));
        var newer = Chat("fake", "newer", updated: At(2026, 9, 30));
        var undated = Chat("fake", "undated", updated: null);
        var catalog = new FakeCatalog("fake") { Chats = [undated, older, newer] };

        var library = ChatLibrary.List(Config(("fake", Remote)), new SharedConfig(), _syncRoot, [catalog]);

        Assert.Equal(["newer", "older", "undated"], Assert.Single(library).Chats.Select(chat => chat.Id));
    }

    [Fact]
    public void List_ShowsAProjectThatHasNoChatsYet()
    {
        var library = ChatLibrary.List(
            Config(("fake", Remote)), new SharedConfig(), _syncRoot, [new FakeCatalog("fake")]);

        Assert.Empty(Assert.Single(library).Chats);
    }

    [Fact]
    public void List_ListsEveryEnabledProviderOfAProjectSeparately()
    {
        var first = new FakeCatalog("first") { Chats = [Chat("first", "a", updated: At(2026, 9, 1))] };
        var second = new FakeCatalog("second") { Chats = [Chat("second", "b", updated: At(2026, 9, 2))] };

        var library = ChatLibrary.List(
            Config(("first", Remote), ("second", Remote)), new SharedConfig(), _syncRoot, [first, second]);

        Assert.Equal(["first", "second"], library.Select(project => project.ProviderId).Order());
    }

    [Fact]
    public void List_SkipsAProviderThatCannotDescribeAnArchive()
    {
        var library = ChatLibrary.List(
            Config(("plain", Remote)), new SharedConfig(), _syncRoot, [new FakeChatProvider(_root.Path) { Id = "plain" }]);

        Assert.Empty(library);
    }

    /// <summary>
    /// A project that cannot sync here — its folder was moved or never cloned on this PC —
    /// still has archived chats, and hiding them would look like data loss.
    /// </summary>
    [Fact]
    public void List_StillShowsAProjectWhoseLocalFolderIsGone()
    {
        var config = new LocalConfig { SyncRootPath = _syncRoot };
        config.AddOrUpdate(ProjectIdentity.FromRemote(Remote), Path.Combine(_root.Path, "does-not-exist"), "fake");
        var catalog = new FakeCatalog("fake") { Chats = [Chat("fake", "kept", updated: At(2026, 9, 1))] };

        var library = ChatLibrary.List(config, new SharedConfig(), _syncRoot, [catalog]);

        Assert.Equal("kept", Assert.Single(Assert.Single(library).Chats).Id);
    }

    [Fact]
    public void List_SkipsARegistrationWhoseRemoteCannotBeParsed()
    {
        var config = new LocalConfig { SyncRootPath = _syncRoot };
        config.Projects.Add(new LocalProjectEntry { Remote = "   ", LocalPath = _root.Path, ProviderIds = ["fake"] });
        var catalog = new FakeCatalog("fake");

        var library = ChatLibrary.List(config, new SharedConfig(), _syncRoot, [catalog]);

        Assert.Empty(library);
        Assert.Empty(catalog.RequestedFolders);
    }

    /// <summary>
    /// The shared mapping comes from the sync repository, so a hand-edited or hostile name
    /// must never be allowed to point the listing outside the project's own folder.
    /// </summary>
    [Theory]
    [InlineData("..")]
    [InlineData("../escape")]
    [InlineData("a\\b")]
    public void List_RefusesAProjectNameThatWouldEscapeItsFolder(string name)
    {
        var shared = new SharedConfig();
        shared.Projects.Add(new SharedProjectEntry
        {
            Remote = ProjectIdentity.FromRemote(Remote).NormalizedRemote,
            Name = name
        });
        var catalog = new FakeCatalog("fake");

        var library = ChatLibrary.List(Config(("fake", Remote)), shared, _syncRoot, [catalog]);

        Assert.Empty(library);
        Assert.Empty(catalog.RequestedFolders);
    }

    private LocalConfig Config(params (string ProviderId, string Remote)[] registrations)
    {
        var config = new LocalConfig { SyncRootPath = _syncRoot };
        var localPath = Directory.CreateDirectory(_root.Combine("repo")).FullName;
        foreach (var (providerId, remote) in registrations)
        {
            config.AddOrUpdate(ProjectIdentity.FromRemote(remote), localPath, providerId);
        }

        return config;
    }

    private static DateTimeOffset At(int year, int month, int day) => new(year, month, day, 12, 0, 0, TimeSpan.Zero);

    private static ArchivedChat Chat(string providerId, string id, DateTimeOffset? updated) =>
        new(providerId, id, $"Chat {id}", updated, updated, 1024, 1);

    private sealed class FakeCatalog(string id) : IArchivedChatCatalog
    {
        public string Id { get; } = id;

        public IReadOnlyList<string> ProcessNames { get; } = [];

        public IReadOnlyList<ArchivedChat> Chats { get; init; } = [];

        public List<string> RequestedFolders { get; } = [];

        public IReadOnlyList<ArchivedChat> ListArchivedChats(string projectSyncFolder)
        {
            RequestedFolders.Add(projectSyncFolder);
            return Chats;
        }

        public IEnumerable<ChatLocation> Discover(ProjectInfo project) => [];

        public string MapToLocal(ProjectInfo project, string relativePath) => throw new NotSupportedException();
    }
}
