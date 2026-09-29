using CodeChatSync.Core;
using CodeChatSync.Providers.Claude;
using CodeChatSync.Tests.TestSupport;

namespace CodeChatSync.Tests;

public class ClaudeTranscriptRestoreCwdTests : IDisposable
{
    private const string SessionId = "11111111-1111-4111-8111-111111111111";
    private readonly ClaudeProjectsFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void Sync_ArchivedTranscriptWithForeignCwd_RefusesRestoreToRegisteredLocalPath()
    {
        var registeredPath = _fixture.CreateLocalProject("this-pc", "client-app");
        var foreignPath = _fixture.CreateLocalProject("other-pc", "client-app");
        var project = ClaudeProjectsFixture.Project(registeredPath);
        var syncRoot = _fixture.Combine("sync");
        var archived = ArchiveTranscript(syncRoot, project, foreignPath);
        var state = new SyncState();

        AssertRefusedRestore(project, syncRoot, state, archived);
    }

    [Fact]
    public void Sync_ArchivedTranscriptWithoutCwd_RefusesRestoreToRegisteredLocalPath()
    {
        var registeredPath = _fixture.CreateLocalProject("this-pc", "client-app");
        var project = ClaudeProjectsFixture.Project(registeredPath);
        var syncRoot = _fixture.Combine("sync");
        var archived = ArchiveTranscript(syncRoot, project, cwd: null);
        var state = new SyncState();

        AssertRefusedRestore(project, syncRoot, state, archived);
    }

    [Fact]
    public void Sync_ArchivedTranscriptWithMatchingCwd_RestoresToRegisteredLocalPath()
    {
        var registeredPath = _fixture.CreateLocalProject("this-pc", "client-app");
        var project = ClaudeProjectsFixture.Project(registeredPath);
        var syncRoot = _fixture.Combine("sync");
        var archived = ArchiveTranscript(syncRoot, project, registeredPath);
        var state = new SyncState();

        var report = Sync(project, syncRoot, state);

        var entry = Assert.Single(report.Entries);
        Assert.Equal($"{SessionId}.jsonl", entry.RelativePath);
        Assert.Equal(SyncAction.Pulled, entry.Action);
        Assert.Equal(1, report.PulledCount);
        var restored = RestoredPath(registeredPath);
        Assert.Equal(File.ReadAllText(archived), File.ReadAllText(restored));
        Assert.Equal(SyncState.ComputeHash(archived), state.GetBaseline($"{SessionId}.jsonl"));
    }

    private void AssertRefusedRestore(ProjectInfo project, string syncRoot, SyncState state, string archived)
    {
        var originalArchive = File.ReadAllText(archived);
        try
        {
            var report = Sync(project, syncRoot, state);
            var entry = Assert.Single(report.Entries);
            Assert.Equal($"{SessionId}.jsonl", entry.RelativePath);
            Assert.Equal(SyncAction.Skipped, entry.Action);
            Assert.Equal(0, report.PulledCount);
            Assert.True(ExplainsCwdRefusal(entry.Reason), $"Expected an explicit cwd/path refusal, got: {entry.Reason}");
        }
        catch (InvalidOperationException exception)
        {
            Assert.True(ExplainsCwdRefusal(exception.Message), $"Expected an explicit cwd/path refusal, got: {exception.Message}");
        }

        Assert.False(File.Exists(RestoredPath(project.LocalPath)));
        Assert.Null(state.GetBaseline($"{SessionId}.jsonl"));
        Assert.Equal(originalArchive, File.ReadAllText(archived));
    }

    private static bool ExplainsCwdRefusal(string? reason) =>
        reason is not null && (reason.Contains("cwd", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("working directory", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("path", StringComparison.OrdinalIgnoreCase));

    private string ArchiveTranscript(string syncRoot, ProjectInfo project, string? cwd)
    {
        var source = _fixture.WriteTranscript("archive-source", SessionId, cwd);
        var archived = Path.Combine(syncRoot, "claudecode", project.SyncFolderName, $"{SessionId}.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(archived)!);
        File.Copy(source, archived);
        return archived;
    }

    private SyncReport Sync(ProjectInfo project, string syncRoot, SyncState state) =>
        new ChatSyncService(new FakeProcessGuard()).Sync(
            new ClaudeCodeChatProvider(new FakeProcessGuard(), _fixture.ProjectsRoot),
            project, syncRoot, state);

    private string RestoredPath(string localPath)
    {
        Assert.True(ClaudeProjectFolderName.TryGet(localPath, out var folder));
        return Path.Combine(_fixture.ProjectsRoot, folder, $"{SessionId}.jsonl");
    }
}
