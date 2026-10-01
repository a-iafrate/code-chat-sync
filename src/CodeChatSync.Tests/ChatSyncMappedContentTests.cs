using System.Text;
using CodeChatSync.Core;
using CodeChatSync.Tests.TestSupport;

namespace CodeChatSync.Tests;

public sealed class ChatSyncMappedContentTests : IDisposable
{
    private const string RelativePath = "session-1/workspace.yaml";
    private const string ProjectToken = "${project}";

    private readonly TempDirectory _root = new();
    private readonly string _localRoot;
    private readonly string _syncRoot;
    private readonly ProjectInfo _project;

    public ChatSyncMappedContentTests()
    {
        _localRoot = _root.Combine("local-project");
        _syncRoot = _root.Combine("sync");
        Directory.CreateDirectory(_localRoot);
        Directory.CreateDirectory(_syncRoot);
        _project = new ProjectInfo
        {
            Identity = ProjectIdentity.FromRemote("https://github.com/example/mapped-project.git"),
            LocalPath = _localRoot
        };
    }

    public void Dispose() => _root.Dispose();

    [Fact]
    public void Sync_PushesPortableBytesAndUsesTheirHashAsBaseline()
    {
        var localBytes = Utf8($"cwd: {_localRoot}\\src\r\nbranch: feature\r\n");
        var expectedPortable = Utf8($"cwd: {ProjectToken}\\src\r\nbranch: feature\r\n");
        WriteLocal(localBytes);
        var provider = new MappedFakeProvider(_localRoot);
        var state = new SyncState();

        var report = Sync(provider, state);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(SyncAction.Pushed, entry.Action);
        Assert.Equal(expectedPortable, File.ReadAllBytes(SyncFile()));
        Assert.Equal(SyncState.ComputeHash(expectedPortable), state.GetBaseline(RelativePath));
        Assert.Equal(1, provider.ToPortableCalls);
    }

    [Fact]
    public void Sync_PullsLocalBytesAndBacksUpExistingFile()
    {
        var previousLocal = Utf8($"cwd: {_localRoot}\\old\r\nbranch: main\r\n");
        var previousPortable = Utf8($"cwd: {ProjectToken}\\old\r\nbranch: main\r\n");
        var nextPortable = Utf8($"cwd: {ProjectToken}\\new\r\nbranch: feature\r\n");
        var expectedLocal = Utf8($"cwd: {_localRoot}\\new\r\nbranch: feature\r\n");
        WriteLocal(previousLocal);
        WriteSync(nextPortable);
        var state = new SyncState();
        state.SetBaseline(RelativePath, SyncState.ComputeHash(previousPortable));

        var report = Sync(new MappedFakeProvider(_localRoot), state);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(SyncAction.Pulled, entry.Action);
        Assert.Equal(expectedLocal, File.ReadAllBytes(LocalFile()));
        Assert.NotNull(entry.BackupPath);
        Assert.StartsWith(ChatSyncService.GetBackupDirectory(_syncRoot), entry.BackupPath!, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(Path.Combine("session-1", "workspace.yaml"), entry.BackupPath!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(previousLocal, File.ReadAllBytes(entry.BackupPath!));
        Assert.Equal(SyncState.ComputeHash(nextPortable), state.GetBaseline(RelativePath));
    }

    [Fact]
    public void Sync_BlocksMappedPullWhileProviderRunsAndLeavesLocalFileAndBaselineUntouched()
    {
        var previousLocal = Utf8($"cwd: {_localRoot}\\old\r\n");
        var previousPortable = Utf8($"cwd: {ProjectToken}\\old\r\n");
        var remotePortable = Utf8($"cwd: {ProjectToken}\\remote\r\n");
        WriteLocal(previousLocal);
        WriteSync(remotePortable);
        var state = new SyncState();
        var oldBaseline = SyncState.ComputeHash(previousPortable);
        state.SetBaseline(RelativePath, oldBaseline);

        var report = Sync(new MappedFakeProvider(_localRoot), state, new FakeProcessGuard("devenv"));

        var entry = Assert.Single(report.Entries);
        Assert.Equal(SyncAction.Skipped, entry.Action);
        Assert.True(entry.IsBlockedByProvider);
        Assert.True(report.HasBlockedPulls);
        Assert.Equal(previousLocal, File.ReadAllBytes(LocalFile()));
        Assert.Null(entry.BackupPath);
        Assert.Equal(oldBaseline, state.GetBaseline(RelativePath));
        Assert.False(Directory.Exists(ChatSyncService.GetBackupDirectory(_syncRoot)));
    }

    [Fact]
    public void Sync_ReportsUnchangedAndDoesNotRewriteAlreadyLocalMappedContent()
    {
        var localBytes = Utf8($"cwd: {_localRoot}\\src\r\nbranch: main\r\n");
        var portableBytes = Utf8($"cwd: {ProjectToken}\\src\r\nbranch: main\r\n");
        WriteLocal(localBytes);
        WriteSync(portableBytes);
        var sentinelWriteTime = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(LocalFile(), sentinelWriteTime);
        var provider = new MappedFakeProvider(_localRoot);
        var state = new SyncState();

        var report = Sync(provider, state);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(SyncAction.Unchanged, entry.Action);
        Assert.Equal(localBytes, File.ReadAllBytes(LocalFile()));
        Assert.Equal(sentinelWriteTime, File.GetLastWriteTimeUtc(LocalFile()));
        Assert.Equal(SyncState.ComputeHash(portableBytes), state.GetBaseline(RelativePath));
        Assert.Equal(1, provider.ToLocalCalls);
        Assert.False(Directory.Exists(ChatSyncService.GetBackupDirectory(_syncRoot)));
    }

    [Fact]
    public void Sync_PullsAndBacksUpWhenPortableContentNeedsLocalRemapping()
    {
        var existingBytes = Utf8($"cwd: {ProjectToken}\\legacy\r\nbranch: main\r\n");
        var expectedLocal = Utf8($"cwd: {_localRoot}\\legacy\r\nbranch: main\r\n");
        WriteLocal(existingBytes);
        WriteSync(existingBytes);
        var provider = new MappedFakeProvider(_localRoot);
        var state = new SyncState();

        var report = Sync(provider, state);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(SyncAction.Pulled, entry.Action);
        Assert.Equal(expectedLocal, File.ReadAllBytes(LocalFile()));
        Assert.NotNull(entry.BackupPath);
        Assert.Equal(existingBytes, File.ReadAllBytes(entry.BackupPath!));
        Assert.Equal(SyncState.ComputeHash(existingBytes), state.GetBaseline(RelativePath));
        Assert.Equal(1, provider.ToPortableCalls);
        Assert.Equal(2, provider.ToLocalCalls);
    }

    [Fact]
    public void ComputeHash_ReadOnlySpanMatchesHashOfFileBytes()
    {
        var bytes = Utf8("mapped bytes\0with a binary boundary\r\n");
        var path = _root.WriteFile("hash-input.bin", "");
        File.WriteAllBytes(path, bytes);

        var spanHash = SyncState.ComputeHash((ReadOnlySpan<byte>)bytes);
        var pathHash = SyncState.ComputeHash(path);

        Assert.Equal(pathHash, spanHash);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), spanHash);
    }

    [Fact]
    public void Sync_PullsNewPortableContentFromLegacyRawBaselineAndMigratesBaseline()
    {
        var previousLocal = Utf8($"cwd: {_localRoot}\\old\r\nbranch: main\r\n");
        var nextPortable = Utf8($"cwd: {ProjectToken}\\new\r\nbranch: feature\r\n");
        var expectedLocal = Utf8($"cwd: {_localRoot}\\new\r\nbranch: feature\r\n");
        WriteLocal(previousLocal);
        WriteSync(nextPortable);
        var state = new SyncState();
        var legacyBaseline = SyncState.ComputeHash(LocalFile());
        state.SetBaseline(RelativePath, legacyBaseline);
        var provider = new MappedFakeProvider(_localRoot);

        var report = Sync(provider, state);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(SyncAction.Pulled, entry.Action);
        Assert.Equal(expectedLocal, File.ReadAllBytes(LocalFile()));
        Assert.Equal(nextPortable, File.ReadAllBytes(SyncFile()));
        Assert.NotNull(entry.BackupPath);
        Assert.Equal(previousLocal, File.ReadAllBytes(entry.BackupPath!));
        Assert.Equal(SyncState.ComputeHash(nextPortable), state.GetBaseline(RelativePath));
        var backupCount = Directory.GetFiles(ChatSyncService.GetBackupDirectory(_syncRoot), "*", SearchOption.AllDirectories).Length;

        var secondReport = Sync(provider, state);

        Assert.Equal(SyncAction.Unchanged, Assert.Single(secondReport.Entries).Action);
        Assert.Equal(expectedLocal, File.ReadAllBytes(LocalFile()));
        Assert.Equal(SyncState.ComputeHash(nextPortable), state.GetBaseline(RelativePath));
        Assert.Equal(backupCount, Directory.GetFiles(ChatSyncService.GetBackupDirectory(_syncRoot), "*", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public void Sync_ReportsConflictWhenLocalRawContentAndRemotePortableContentBothChangedFromLegacyBaseline()
    {
        var previousLocal = Utf8($"cwd: {_localRoot}\\old\r\nbranch: main\r\n");
        var editedLocal = Utf8($"cwd: {_localRoot}\\local-edit\r\nbranch: local-feature\r\n");
        var changedRemote = Utf8($"cwd: {ProjectToken}\\remote-edit\r\nbranch: remote-feature\r\n");
        WriteLocal(previousLocal);
        var state = new SyncState();
        var legacyBaseline = SyncState.ComputeHash(LocalFile());
        state.SetBaseline(RelativePath, legacyBaseline);
        WriteLocal(editedLocal);
        WriteSync(changedRemote);

        var report = Sync(new MappedFakeProvider(_localRoot), state);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(SyncAction.Conflict, entry.Action);
        Assert.True(report.HasConflicts);
        Assert.Equal(editedLocal, File.ReadAllBytes(LocalFile()));
        Assert.Equal(changedRemote, File.ReadAllBytes(SyncFile()));
        Assert.Null(entry.BackupPath);
        Assert.Equal(legacyBaseline, state.GetBaseline(RelativePath));
        Assert.False(Directory.Exists(ChatSyncService.GetBackupDirectory(_syncRoot)));
    }

    [Fact]
    public void Sync_PushesLocalEditWhenArchiveStillMatchesLegacyRawBaseline()
    {
        var previousLocal = Utf8($"cwd: {_localRoot}\\src\r\nbranch: main\r\n");
        var editedLocal = Utf8($"cwd: {_localRoot}\\src\r\nbranch: feature\r\n");
        var expectedPortable = Utf8($"cwd: {ProjectToken}\\src\r\nbranch: feature\r\n");
        WriteLocal(previousLocal);
        WriteSync(previousLocal);
        var state = new SyncState();
        var legacyBaseline = SyncState.ComputeHash(LocalFile());
        state.SetBaseline(RelativePath, legacyBaseline);
        WriteLocal(editedLocal);

        var report = Sync(new MappedFakeProvider(_localRoot), state);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(SyncAction.Pushed, entry.Action);
        Assert.Equal(editedLocal, File.ReadAllBytes(LocalFile()));
        Assert.Equal(expectedPortable, File.ReadAllBytes(SyncFile()));
        Assert.Equal(SyncState.ComputeHash(expectedPortable), state.GetBaseline(RelativePath));
        Assert.Null(entry.BackupPath);
        Assert.False(Directory.Exists(ChatSyncService.GetBackupDirectory(_syncRoot)));
    }
    private SyncReport Sync(MappedFakeProvider provider, SyncState state, IProcessGuard? guard = null) =>
        new ChatSyncService(guard ?? new FakeProcessGuard()).Sync(provider, _project, _syncRoot, state);

    private string LocalFile() => Path.Combine(_localRoot, "session-1", "workspace.yaml");

    private string SyncFile() =>
        Path.Combine(_syncRoot, "mapped-fake", _project.Identity.Slug, "session-1", "workspace.yaml");

    private void WriteLocal(byte[] content) => Write(LocalFile(), content);

    private void WriteSync(byte[] content) => Write(SyncFile(), content);

    private static void Write(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);

    private sealed class MappedFakeProvider(string localRoot) : IChatProvider, IChatContentMapper
    {
        public string Id => "mapped-fake";

        public IReadOnlyList<string> ProcessNames { get; } = ["devenv"];

        public int ToPortableCalls { get; private set; }

        public int ToLocalCalls { get; private set; }

        public IEnumerable<ChatLocation> Discover(ProjectInfo project)
        {
            if (!Directory.Exists(localRoot))
            {
                yield break;
            }

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

        public byte[] ToPortable(ProjectInfo project, byte[] localContent)
        {
            ToPortableCalls++;
            return Replace(localContent, project.LocalPath, ProjectToken);
        }

        public byte[] ToLocal(ProjectInfo project, byte[] portableContent)
        {
            ToLocalCalls++;
            return Replace(portableContent, ProjectToken, project.LocalPath);
        }

        private static byte[] Replace(byte[] content, string oldValue, string newValue)
        {
            var text = Encoding.UTF8.GetString(content);
            return Utf8(text.Replace(oldValue, newValue, StringComparison.OrdinalIgnoreCase));
        }
    }
}