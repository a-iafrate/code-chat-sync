using CodeChatSync.Core;
using CodeChatSync.Tests.TestSupport;

namespace CodeChatSync.Tests;

public class ChatSyncServiceTests : IDisposable
{
    private const string RelativePath = "session-1/events.jsonl";
    private const string ProcessName = "devenv";

    private readonly TempDirectory _root = new();
    private readonly string _localRoot;
    private readonly string _syncRoot;
    private readonly ProjectInfo _project;

    public ChatSyncServiceTests()
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

    [Fact]
    public void Sync_CopiesNewLocalFileIntoTheSyncFolder()
    {
        WriteLocal("local content");
        var state = new SyncState();

        var report = Sync(state);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(SyncAction.Pushed, entry.Action);
        Assert.Equal("local content", File.ReadAllText(SyncFile()));
        Assert.Equal(SyncState.ComputeHash(LocalFile()), state.GetBaseline(RelativePath));
    }

    [Fact]
    public void Sync_CopiesNewSyncFileOntoThisPc()
    {
        WriteSync("remote content");
        var state = new SyncState();

        var report = Sync(state);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(SyncAction.Pulled, entry.Action);
        Assert.Equal("remote content", File.ReadAllText(LocalFile()));
        Assert.Null(entry.BackupPath);
    }

    [Fact]
    public void Sync_BacksUpTheLocalFileBeforeOverwritingIt()
    {
        WriteLocal("local content");
        WriteSync("local content");
        var state = new SyncState();
        state.SetBaseline(RelativePath, SyncState.ComputeHash(LocalFile())!);
        WriteSync("newer remote content");

        var report = Sync(state);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(SyncAction.Pulled, entry.Action);
        Assert.Equal("newer remote content", File.ReadAllText(LocalFile()));
        Assert.NotNull(entry.BackupPath);
        Assert.StartsWith(ChatSyncService.GetBackupDirectory(_syncRoot), entry.BackupPath!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("local content", File.ReadAllText(entry.BackupPath!));
    }

    [Fact]
    public void Sync_ReportsUnchangedWhenBothSidesMatch()
    {
        WriteLocal("same content");
        WriteSync("same content");
        var state = new SyncState();

        var report = Sync(state);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(SyncAction.Unchanged, entry.Action);
        Assert.Equal(SyncState.ComputeHash(LocalFile()), state.GetBaseline(RelativePath));
    }

    [Fact]
    public void Sync_ReportsConflictAndLeavesBothSidesUntouched()
    {
        WriteLocal("local edit");
        WriteSync("remote edit");
        var state = new SyncState();
        state.SetBaseline(RelativePath, "PREVIOUSLY-SYNCED-HASH");

        var report = Sync(state);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(SyncAction.Conflict, entry.Action);
        Assert.True(report.HasConflicts);
        Assert.Equal("local edit", File.ReadAllText(LocalFile()));
        Assert.Equal("remote edit", File.ReadAllText(SyncFile()));
        Assert.Equal("PREVIOUSLY-SYNCED-HASH", state.GetBaseline(RelativePath));
    }

    [Fact]
    public void Sync_LeavesLocalDataAloneWhileTheProviderIsRunning()
    {
        WriteSync("remote content");
        var state = new SyncState();

        var report = Sync(state, ProcessName);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(SyncAction.Skipped, entry.Action);
        Assert.True(entry.IsBlockedByProvider);
        Assert.True(report.HasBlockedPulls);
        Assert.Contains(ProcessName, entry.Reason);
        Assert.False(File.Exists(LocalFile()));
    }

    [Fact]
    public void Sync_StillPushesOtherFilesWhileAPullIsBlocked()
    {
        WriteLocal("local content");
        Write(Path.Combine(_syncRoot, "fake", _project.Identity.Slug, "session-2", "events.jsonl"), "remote only");
        var state = new SyncState();

        var report = Sync(state, ProcessName);

        Assert.Equal(1, report.PushedCount);
        Assert.Equal(1, report.BlockedByProviderCount);
        Assert.Equal("local content", File.ReadAllText(SyncFile()));
    }

    [Fact]
    public void Sync_CompletesABlockedPullOnceTheProviderIsClosed()
    {
        WriteSync("remote content");
        var state = new SyncState();

        var blocked = Sync(state, ProcessName);
        Assert.True(blocked.HasBlockedPulls);

        var completed = Sync(state);

        Assert.Equal(SyncAction.Pulled, Assert.Single(completed.Entries).Action);
        Assert.Equal("remote content", File.ReadAllText(LocalFile()));
    }

    [Fact]
    public void Sync_PushesWhileTheProviderIsRunning()
    {
        WriteLocal("local content");
        var state = new SyncState();

        var report = Sync(state, ProcessName);

        Assert.Equal(SyncAction.Pushed, Assert.Single(report.Entries).Action);
        Assert.Equal("local content", File.ReadAllText(SyncFile()));
    }

    [Fact]
    public void Sync_SkipsLocalDeletionsInsteadOfPropagatingThem()
    {
        WriteSync("remote content");
        var state = new SyncState();
        state.SetBaseline(RelativePath, SyncState.ComputeHash(SyncFile())!);

        var report = Sync(state);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(SyncAction.Skipped, entry.Action);
        Assert.True(File.Exists(SyncFile()));
    }

    [Fact]
    public void Sync_SkipsSyncFolderDeletionsInsteadOfPropagatingThem()
    {
        WriteLocal("local content");
        var state = new SyncState();
        state.SetBaseline(RelativePath, SyncState.ComputeHash(LocalFile())!);

        var report = Sync(state);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(SyncAction.Skipped, entry.Action);
        Assert.True(File.Exists(LocalFile()));
    }

    [Fact]
    public void Sync_DryRunReportsActionsWithoutCopyingAnything()
    {
        WriteLocal("local content");
        var state = new SyncState();

        var report = Sync(state, dryRun: true);

        Assert.Equal(1, report.PushedCount);
        Assert.False(File.Exists(SyncFile()));
        Assert.Null(state.GetBaseline(RelativePath));
    }

    [Fact]
    public void Sync_KeepsProviderAndProjectFoldersSeparateInTheSyncRoot()
    {
        WriteLocal("local content");

        Sync(new SyncState());

        var expected = Path.Combine(_syncRoot, "fake", _project.Identity.Slug, "session-1", "events.jsonl");
        Assert.True(File.Exists(expected));
    }

    [Fact]
    public void Sync_SkipsChatsTheProviderReportsAsOpen()
    {
        WriteLocal("local content");
        var provider = new FakeChatProvider(_localRoot, ProcessName);
        provider.InUseRelativePaths.Add(RelativePath);
        var state = new SyncState();

        var report = new ChatSyncService(new FakeProcessGuard()).Sync(provider, _project, _syncRoot, state);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(SyncAction.Skipped, entry.Action);
        Assert.Equal("The chat is currently open.", entry.Reason);
        Assert.False(File.Exists(SyncFile()));
    }

    [Fact]
    public void Sync_DoesNotOverwriteAnOpenLocalChat()
    {
        WriteLocal("open locally");
        WriteSync("newer remote content");
        var provider = new FakeChatProvider(_localRoot);
        provider.InUseRelativePaths.Add(RelativePath);
        var state = new SyncState();
        state.SetBaseline(RelativePath, SyncState.ComputeHash(LocalFile())!);

        var report = new ChatSyncService(new FakeProcessGuard()).Sync(provider, _project, _syncRoot, state);

        Assert.Equal(SyncAction.Skipped, Assert.Single(report.Entries).Action);
        Assert.Equal("open locally", File.ReadAllText(LocalFile()));
    }

    private SyncReport Sync(SyncState state, string? runningProcess = null, bool dryRun = false)
    {
        var provider = new FakeChatProvider(_localRoot, ProcessName);
        var guard = runningProcess is null ? new FakeProcessGuard() : new FakeProcessGuard(runningProcess);
        return new ChatSyncService(guard).Sync(provider, _project, _syncRoot, state, dryRun);
    }

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

    [Fact]
    public void Sync_RestoresOnlyPathsAcceptedByTheSelectionPredicate()
    {
        var selectedPath = "session-selected/events.jsonl";
        var unselectedPath = "session-unselected/events.jsonl";
        Write(Path.Combine(_syncRoot, "fake", _project.Identity.Slug, selectedPath), "selected remote");
        Write(Path.Combine(_syncRoot, "fake", _project.Identity.Slug, unselectedPath), "unselected remote");
        var state = new SyncState();

        var report = new ChatSyncService(new FakeProcessGuard()).Sync(
            new FakeChatProvider(_localRoot),
            _project,
            _syncRoot,
            state,
            shouldRestore: path => path == selectedPath);

        Assert.Equal(1, report.PulledCount);
        Assert.Equal(1, report.SkippedCount);
        Assert.Equal("selected remote", File.ReadAllText(Path.Combine(_localRoot, selectedPath.Replace('/', Path.DirectorySeparatorChar))));
        Assert.False(File.Exists(Path.Combine(_localRoot, unselectedPath.Replace('/', Path.DirectorySeparatorChar))));
        Assert.Equal(SyncState.ComputeHash(LocalFileFor(selectedPath)), state.GetBaseline(selectedPath));
        Assert.Null(state.GetBaseline(unselectedPath));
        Assert.False(Directory.Exists(ChatSyncService.GetBackupDirectory(_syncRoot)));
    }

    [Fact]
    public void Sync_PreservesAnExistingLocalFileWhenItsRemoteChangeIsUnselected()
    {
        const string localContent = "local version";
        const string remoteContent = "remote version";
        WriteLocal(localContent);
        WriteSync(localContent);
        var state = new SyncState();
        state.SetBaseline(RelativePath, SyncState.ComputeHash(LocalFile())!);
        WriteSync(remoteContent);

        var report = SyncWithRestorePredicate(state, _ => false);

        Assert.Equal(SyncAction.Skipped, Assert.Single(report.Entries).Action);
        Assert.Equal(localContent, File.ReadAllText(LocalFile()));
        Assert.Equal(remoteContent, File.ReadAllText(SyncFile()));
        Assert.Equal(SyncState.ComputeHash(LocalFile()), state.GetBaseline(RelativePath));
        Assert.False(Directory.Exists(ChatSyncService.GetBackupDirectory(_syncRoot)));
    }

    [Fact]
    public void Sync_StillPushesLocalEditsWhenTheRestorePredicateRejectsThePath()
    {
        WriteLocal("previously synced");
        WriteSync("previously synced");
        var state = new SyncState();
        state.SetBaseline(RelativePath, SyncState.ComputeHash(LocalFile())!);
        WriteLocal("local edit");

        var report = SyncWithRestorePredicate(state, _ => false);

        Assert.Equal(SyncAction.Pushed, Assert.Single(report.Entries).Action);
        Assert.Equal("local edit", File.ReadAllText(SyncFile()));
        Assert.Equal(SyncState.ComputeHash(LocalFile()), state.GetBaseline(RelativePath));
    }

    private SyncReport SyncWithRestorePredicate(SyncState state, Func<string, bool> shouldRestore) =>
        new ChatSyncService(new FakeProcessGuard()).Sync(
            new FakeChatProvider(_localRoot), _project, _syncRoot, state, shouldRestore: shouldRestore);

    private string LocalFileFor(string relativePath) =>
        Path.Combine(_localRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));}
