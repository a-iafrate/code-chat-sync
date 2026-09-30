using System.Text;
using CodeChatSync.Core;
using CodeChatSync.Tests.TestSupport;

namespace CodeChatSync.Tests;

/// <summary>
/// Some tools only list a chat that appears in their own index, so copying the files is
/// not enough to make a restored chat visible.
/// </summary>
public sealed class ChatSyncSessionRegistrationTests : IDisposable
{
    private readonly TempDirectory _root = new();
    private readonly string _localRoot;
    private readonly string _syncRoot;
    private readonly ProjectInfo _project;

    public ChatSyncSessionRegistrationTests()
    {
        _localRoot = _root.Combine("local");
        _syncRoot = _root.Combine("sync");
        Directory.CreateDirectory(_localRoot);
        Directory.CreateDirectory(_syncRoot);
        _project = new ProjectInfo
        {
            Identity = ProjectIdentity.FromRemote("https://github.com/example/registered.git"),
            LocalPath = _localRoot
        };
    }

    public void Dispose() => _root.Dispose();

    /// <summary>
    /// The case that matters in practice: the files were restored by an earlier run, so
    /// nothing is copied now and the registration is the only thing left to do.
    /// </summary>
    [Fact]
    public void Sync_RegistersSessionsEvenWhenNoFileNeedsCopying()
    {
        WriteLocal("session-a/events.jsonl", "same");
        WriteSync("session-a/events.jsonl", "same");
        var provider = new RegisteringProvider(_localRoot);

        var report = Sync(provider);

        Assert.Equal(SyncAction.Unchanged, Assert.Single(report.Entries).Action);
        Assert.Equal(["session-a"], provider.RegisteredSessionIds);
        Assert.Equal(1, report.RegisteredSessionCount);
    }

    [Fact]
    public void Sync_RegistersEachSessionOnceAcrossItsFiles()
    {
        WriteLocal("session-a/events.jsonl", "one");
        WriteLocal("session-a/workspace.yaml", "two");
        WriteLocal("session-b/events.jsonl", "three");
        var provider = new RegisteringProvider(_localRoot);

        Sync(provider);

        Assert.Equal(["session-a", "session-b"], provider.RegisteredSessionIds.Order());
    }

    [Fact]
    public void Sync_DoesNotRegisterSessionsThisPcHasNotOptedIntoRestoring()
    {
        WriteSync("session-a/events.jsonl", "kept out");
        WriteSync("session-b/events.jsonl", "wanted");
        var provider = new RegisteringProvider(_localRoot);

        Sync(provider, shouldRestore: path => path.StartsWith("session-b", StringComparison.Ordinal));

        Assert.Equal(["session-b"], provider.RegisteredSessionIds);
    }

    [Fact]
    public void Sync_WaitsForTheToolToCloseBeforeTouchingItsIndex()
    {
        WriteLocal("session-a/events.jsonl", "content");
        var provider = new RegisteringProvider(_localRoot);

        var report = Sync(provider, guard: new FakeProcessGuard("devenv"));

        Assert.Empty(provider.RegisteredSessionIds);
        Assert.NotNull(report.Registration);
        Assert.True(report.Registration!.IsBlockedByProvider);
        Assert.True(report.HasBlockedPulls);
        Assert.Contains("devenv", report.Registration.Reason);
    }

    [Fact]
    public void Sync_DryRunAsksTheProviderToReportWithoutWriting()
    {
        WriteLocal("session-a/events.jsonl", "content");
        var provider = new RegisteringProvider(_localRoot);

        Sync(provider, dryRun: true);

        Assert.True(provider.LastDryRun);
    }

    [Fact]
    public void Sync_LeavesTheReportEmptyForProvidersWithoutAnIndex()
    {
        WriteLocal("session-a/events.jsonl", "content");

        var report = new ChatSyncService(new FakeProcessGuard())
            .Sync(new FakeChatProvider(_localRoot), _project, _syncRoot, new SyncState());

        Assert.Null(report.Registration);
        Assert.Equal(0, report.RegisteredSessionCount);
    }

    private SyncReport Sync(
        RegisteringProvider provider,
        IProcessGuard? guard = null,
        bool dryRun = false,
        Func<string, bool>? shouldRestore = null) =>
        new ChatSyncService(guard ?? new FakeProcessGuard())
            .Sync(provider, _project, _syncRoot, new SyncState(), dryRun, shouldRestore);

    private void WriteLocal(string relativePath, string content) =>
        Write(Path.Combine(_localRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)), content);

    private void WriteSync(string relativePath, string content) =>
        Write(
            Path.Combine(_syncRoot, "registering", _project.Identity.Slug,
                relativePath.Replace('/', Path.DirectorySeparatorChar)),
            content);

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
    }

    private sealed class RegisteringProvider(string localRoot) : IChatSessionRegistrar
    {
        private readonly FakeChatProvider _inner = new(localRoot, "devenv");

        public string Id => "registering";

        public IReadOnlyList<string> ProcessNames { get; } = ["devenv"];

        public List<string> RegisteredSessionIds { get; } = [];

        public bool LastDryRun { get; private set; }

        public IEnumerable<ChatLocation> Discover(ProjectInfo project) => _inner.Discover(project);

        public string MapToLocal(ProjectInfo project, string relativePath) =>
            _inner.MapToLocal(project, relativePath);

        public string GetSessionId(string relativePath)
        {
            var separator = relativePath.IndexOf('/');
            if (separator <= 0)
            {
                throw new ArgumentException("Expected a session folder.", nameof(relativePath));
            }

            return relativePath[..separator];
        }

        public SessionRegistrationResult RegisterSessions(
            ProjectInfo project,
            IReadOnlyCollection<string> sessionIds,
            bool dryRun)
        {
            LastDryRun = dryRun;
            RegisteredSessionIds.AddRange(sessionIds);
            return new SessionRegistrationResult { RegisteredCount = sessionIds.Count };
        }
    }
}
