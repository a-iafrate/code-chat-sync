using System.Text;
using CodeChatSync.Core;
using CodeChatSync.Tests.TestSupport;

namespace CodeChatSync.Tests;

/// <summary>
/// Visual Studio was observed recording the same folder as both <c>c:\</c> and
/// <c>C:\</c>, and it may compare that path as an exact string when deciding which chats
/// belong to the open solution. Rewriting a descriptor just to change its letter case
/// could therefore hide a chat that is currently visible.
/// </summary>
public sealed class ChatSyncCasePreservingRewriteTests : IDisposable
{
    private const string RelativePath = "session-1/workspace.yaml";
    private const string ProjectToken = "${project}";

    private readonly TempDirectory _root = new();
    private readonly string _localRoot;
    private readonly string _syncRoot;
    private readonly ProjectInfo _project;

    public ChatSyncCasePreservingRewriteTests()
    {
        _localRoot = _root.Combine("Local-Project");
        _syncRoot = _root.Combine("sync");
        Directory.CreateDirectory(_localRoot);
        Directory.CreateDirectory(_syncRoot);
        _project = new ProjectInfo
        {
            Identity = ProjectIdentity.FromRemote("https://github.com/example/case-project.git"),
            LocalPath = _localRoot
        };
    }

    public void Dispose() => _root.Dispose();

    [Fact]
    public void Sync_LeavesALocalPathThatDiffersOnlyInCaseAlone()
    {
        var localBytes = Utf8($"cwd: {_localRoot.ToLowerInvariant()}\\\r\nbranch: main\r\n");
        var portableBytes = Utf8($"cwd: {ProjectToken}\\\r\nbranch: main\r\n");
        WriteLocal(localBytes);
        WriteSync(portableBytes);

        var report = Sync();

        var entry = Assert.Single(report.Entries);
        Assert.Equal(SyncAction.Unchanged, entry.Action);
        Assert.Equal(localBytes, File.ReadAllBytes(LocalFile()));
    }

    /// <summary>A descriptor still holding the portable form is not a case difference.</summary>
    [Fact]
    public void Sync_StillRewritesContentThatIsNotJustSpeltDifferently()
    {
        var unmappedBytes = Utf8($"cwd: {ProjectToken}\\\r\nbranch: main\r\n");
        WriteLocal(unmappedBytes);
        WriteSync(unmappedBytes);

        var report = Sync();

        var entry = Assert.Single(report.Entries);
        Assert.Equal(SyncAction.Pulled, entry.Action);
        Assert.Equal(Utf8($"cwd: {_localRoot}\\\r\nbranch: main\r\n"), File.ReadAllBytes(LocalFile()));
    }

    private SyncReport Sync() =>
        new ChatSyncService(new FakeProcessGuard())
            .Sync(new CaseMappedProvider(_localRoot), _project, _syncRoot, new SyncState());

    private string LocalFile() => Path.Combine(_localRoot, "session-1", "workspace.yaml");

    private string SyncFile() =>
        Path.Combine(_syncRoot, "case-mapped", _project.Identity.Slug, "session-1", "workspace.yaml");

    private void WriteLocal(byte[] content) => Write(LocalFile(), content);

    private void WriteSync(byte[] content) => Write(SyncFile(), content);

    private static void Write(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);

    private sealed class CaseMappedProvider(string localRoot) : IChatProvider, IChatContentMapper
    {
        public string Id => "case-mapped";

        public IReadOnlyList<string> ProcessNames { get; } = ["devenv"];

        public IEnumerable<ChatLocation> Discover(ProjectInfo project)
        {
            foreach (var file in Directory.EnumerateFiles(localRoot, "*", SearchOption.AllDirectories))
            {
                yield return new ChatLocation
                {
                    RelativePath = Path.GetRelativePath(localRoot, file).Replace('\\', '/'),
                    LocalPath = file,
                    Length = new FileInfo(file).Length,
                    LastWriteTimeUtc = File.GetLastWriteTimeUtc(file)
                };
            }
        }

        public string MapToLocal(ProjectInfo project, string relativePath) =>
            RelativePathGuard.ResolveUnder(localRoot, relativePath);

        public bool IsMapped(string relativePath) =>
            string.Equals(relativePath, RelativePath, StringComparison.OrdinalIgnoreCase);

        public byte[] ToPortable(ProjectInfo project, byte[] localContent) =>
            Replace(localContent, project.LocalPath, ProjectToken);

        public byte[] ToLocal(ProjectInfo project, byte[] portableContent) =>
            Replace(portableContent, ProjectToken, project.LocalPath);

        private static byte[] Replace(byte[] content, string oldValue, string newValue) =>
            Utf8(Encoding.UTF8.GetString(content).Replace(oldValue, newValue, StringComparison.OrdinalIgnoreCase));
    }
}
