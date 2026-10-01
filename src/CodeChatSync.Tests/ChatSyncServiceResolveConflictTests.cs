using CodeChatSync.Core;
using CodeChatSync.Tests.TestSupport;

namespace CodeChatSync.Tests;

/// <summary>
/// Covers forcing one side of a conflict to win, the Core half of the Projects page's
/// "keep local / keep remote" choice.
/// </summary>
public sealed class ChatSyncServiceResolveConflictTests : IDisposable
{
    private const string RelativePath = "session-1/events.jsonl";

    private readonly TempDirectory _root = new();
    private readonly string _localRoot;
    private readonly string _syncRoot;
    private readonly ProjectInfo _project;

    public ChatSyncServiceResolveConflictTests()
    {
        _localRoot = _root.Combine("local");
        _syncRoot = _root.Combine("sync");
        Directory.CreateDirectory(_localRoot);
        Directory.CreateDirectory(_syncRoot);
        _project = new ProjectInfo
        {
            Identity = ProjectIdentity.FromRemote("https://github.com/a-iafrate/code-chat-sync.git"),
            LocalPath = _localRoot
        };
    }

    public void Dispose() => _root.Dispose();

    /// <summary>A conflict is the motivating case, but resolution does not require one.</summary>
    [Fact]
    public void ResolveConflict_KeepLocalPushesThisPcsVersionOverTheSyncFolder()
    {
        WriteLocal("local edit");
        WriteSync("remote edit");
        var state = new SyncState();
        state.SetBaseline(RelativePath, "PREVIOUSLY-SYNCED-HASH");

        var result = ResolveConflict(state, keepLocal: true);

        Assert.Equal(SyncAction.Pushed, result.Action);
        Assert.Equal("local edit", File.ReadAllText(SyncFile()));
        Assert.Equal("local edit", File.ReadAllText(LocalFile()));
        Assert.Equal(SyncState.ComputeHash(LocalFile()), state.GetBaseline(RelativePath));
    }

    [Fact]
    public void ResolveConflict_KeepRemotePullsTheSyncFoldersVersionAndBacksUpTheLocalFile()
    {
        WriteLocal("local edit");
        WriteSync("remote edit");
        var state = new SyncState();
        state.SetBaseline(RelativePath, "PREVIOUSLY-SYNCED-HASH");

        var result = ResolveConflict(state, keepLocal: false);

        Assert.Equal(SyncAction.Pulled, result.Action);
        Assert.Equal("remote edit", File.ReadAllText(LocalFile()));
        Assert.NotNull(result.BackupPath);
        Assert.Equal("local edit", File.ReadAllText(result.BackupPath!));
        Assert.Equal(SyncState.ComputeHash(SyncFile()), state.GetBaseline(RelativePath));
    }

    [Fact]
    public void ResolveConflict_RefusesWhileTheChatIsOpen()
    {
        WriteLocal("local edit");
        WriteSync("remote edit");
        var provider = new FakeChatProvider(_localRoot);
        provider.InUseRelativePaths.Add(RelativePath);

        var result = new ChatSyncService(new FakeProcessGuard())
            .ResolveConflict(provider, _project, _syncRoot, new SyncState(), RelativePath, keepLocal: true);

        Assert.Equal(SyncAction.Skipped, result.Action);
        Assert.Equal("remote edit", File.ReadAllText(SyncFile()));
    }

    [Fact]
    public void ResolveConflict_KeepRemoteWaitsForTheToolToCloseAndLeavesTheLocalFileUntouched()
    {
        WriteLocal("local edit");
        WriteSync("remote edit");
        var state = new SyncState();

        var result = new ChatSyncService(new FakeProcessGuard("devenv"))
            .ResolveConflict(new FakeChatProvider(_localRoot, "devenv"), _project, _syncRoot, state, RelativePath, keepLocal: false);

        Assert.Equal(SyncAction.Skipped, result.Action);
        Assert.True(result.IsBlockedByProvider);
        Assert.Equal("local edit", File.ReadAllText(LocalFile()));
    }

    /// <summary>Keeping local when the file was actually removed here is reported, not guessed at.</summary>
    [Fact]
    public void ResolveConflict_KeepLocalReportsWhenThereIsNoLocalFileToKeep()
    {
        WriteSync("remote edit");
        var state = new SyncState();

        var result = ResolveConflict(state, keepLocal: true);

        Assert.Equal(SyncAction.Skipped, result.Action);
        Assert.Equal("remote edit", File.ReadAllText(SyncFile()));
    }

    /// <summary>Keeping remote when the sync folder has nothing is reported, not guessed at.</summary>
    [Fact]
    public void ResolveConflict_KeepRemoteReportsWhenThereIsNoSyncedFileToKeep()
    {
        WriteLocal("local edit");
        var state = new SyncState();

        var result = ResolveConflict(state, keepLocal: false);

        Assert.Equal(SyncAction.Skipped, result.Action);
        Assert.Equal("local edit", File.ReadAllText(LocalFile()));
    }

    private SyncEntryResult ResolveConflict(SyncState state, bool keepLocal) =>
        new ChatSyncService(new FakeProcessGuard())
            .ResolveConflict(new FakeChatProvider(_localRoot), _project, _syncRoot, state, RelativePath, keepLocal);

    private string LocalFile() => Path.Combine(_localRoot, "session-1", "events.jsonl");

    private string SyncFile() =>
        Path.Combine(_syncRoot, "fake", _project.Identity.Slug, "session-1", "events.jsonl");

    private void WriteLocal(string content) => Write(LocalFile(), content);

    private void WriteSync(string content) => Write(SyncFile(), content);

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
