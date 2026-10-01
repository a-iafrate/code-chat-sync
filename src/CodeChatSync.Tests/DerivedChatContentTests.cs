using System.Text;
using CodeChatSync.Core;
using CodeChatSync.Tests.TestSupport;

namespace CodeChatSync.Tests;

/// <summary>
/// A file a provider derived here from already-synced data must never be published, and
/// must give way to a copy arriving from another PC rather than conflicting with it.
/// Otherwise the same conversation would be stored twice and reported as a clash on
/// content nobody wrote.
/// </summary>
public sealed class DerivedChatContentTests : IDisposable
{
    private const string DerivedPath = "session-1/.chat-window/src/Demo.slnx";
    private const string OrdinaryPath = "session-1/events.jsonl";

    private readonly TempDirectory _root = new();
    private readonly string _localRoot;
    private readonly string _syncRoot;
    private readonly ProjectInfo _project;

    public DerivedChatContentTests()
    {
        _localRoot = _root.Combine("local");
        _syncRoot = _root.Combine("sync");
        Directory.CreateDirectory(_localRoot);
        Directory.CreateDirectory(_syncRoot);
        _project = new ProjectInfo
        {
            Identity = ProjectIdentity.FromRemote("https://github.com/example/derived.git"),
            LocalPath = _localRoot
        };
    }

    public void Dispose() => _root.Dispose();

    [Fact]
    public void Sync_DoesNotPublishAFileTheProviderDerivedHere()
    {
        WriteLocal(DerivedPath, "rebuilt here");
        var state = new SyncState();

        var report = Sync(state);

        Assert.DoesNotContain(report.Entries, entry => entry.Action is SyncAction.Pushed);
        Assert.False(File.Exists(SyncFile(DerivedPath)));
    }

    /// <summary>
    /// The case the user cares about: a chat continued on another PC must arrive, not
    /// collide with the copy this PC derived for itself.
    /// </summary>
    [Fact]
    public void Sync_LetsTheRealCopyFromAnotherPcReplaceTheDerivedOne()
    {
        WriteLocal(DerivedPath, "rebuilt here");
        WriteSync(DerivedPath, "the real record from the other PC");
        var state = new SyncState();

        var report = Sync(state);

        var entry = Assert.Single(report.Entries, item => item.RelativePath == DerivedPath);
        Assert.Equal(SyncAction.Pulled, entry.Action);
        Assert.Equal("the real record from the other PC", File.ReadAllText(LocalFile(DerivedPath)));
        Assert.Equal(0, report.ConflictCount);
    }

    [Fact]
    public void Sync_PublishesTheFileOnceTheToolItselfHasRewrittenIt()
    {
        WriteLocal(DerivedPath, "written by Visual Studio");
        var provider = new DerivedAwareProvider(_localRoot) { DerivedIsStillOurs = false };

        var report = new ChatSyncService(new FakeProcessGuard())
            .Sync(provider, _project, _syncRoot, new SyncState());

        var entry = Assert.Single(report.Entries, item => item.RelativePath == DerivedPath);
        Assert.Equal(SyncAction.Pushed, entry.Action);
        Assert.Equal("written by Visual Studio", File.ReadAllText(SyncFile(DerivedPath)));
    }

    [Fact]
    public void Sync_TreatsEveryOtherFileNormally()
    {
        WriteLocal(OrdinaryPath, "a transcript");
        var state = new SyncState();

        var report = Sync(state);

        var entry = Assert.Single(report.Entries, item => item.RelativePath == OrdinaryPath);
        Assert.Equal(SyncAction.Pushed, entry.Action);
    }

    private SyncReport Sync(SyncState state) =>
        new ChatSyncService(new FakeProcessGuard())
            .Sync(new DerivedAwareProvider(_localRoot), _project, _syncRoot, state);

    private string LocalFile(string relativePath) =>
        Path.Combine(_localRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private string SyncFile(string relativePath) =>
        Path.Combine(_syncRoot, "derived-aware", _project.Identity.Slug,
            relativePath.Replace('/', Path.DirectorySeparatorChar));

    private void WriteLocal(string relativePath, string content) => Write(LocalFile(relativePath), content);

    private void WriteSync(string relativePath, string content) => Write(SyncFile(relativePath), content);

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
    }

    private sealed class DerivedAwareProvider(string localRoot) : IChatProvider, IDerivedChatContent
    {
        private readonly FakeChatProvider _inner = new(localRoot, "devenv");

        public string Id => "derived-aware";

        public IReadOnlyList<string> ProcessNames { get; } = ["devenv"];

        /// <summary>False once the tool has taken the file over.</summary>
        public bool DerivedIsStillOurs { get; init; } = true;

        public IEnumerable<ChatLocation> Discover(ProjectInfo project) =>
            _inner.Discover(project).Where(location =>
                !(DerivedIsStillOurs && location.RelativePath == DerivedPath));

        public string MapToLocal(ProjectInfo project, string relativePath) =>
            _inner.MapToLocal(project, relativePath);

        public bool IsDerivedLocally(ProjectInfo project, string relativePath) =>
            DerivedIsStillOurs && relativePath == DerivedPath;
    }
}
