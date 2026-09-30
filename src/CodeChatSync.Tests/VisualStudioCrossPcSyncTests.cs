using System.Text;
using CodeChatSync.Core;
using CodeChatSync.Providers.VisualStudio;
using CodeChatSync.Tests.TestSupport;

namespace CodeChatSync.Tests;

public sealed class VisualStudioCrossPcSyncTests : IDisposable
{
    private const string SessionId = "session-1";
    private const string DescriptorPath = "session-1/workspace.yaml";
    private const string TranscriptPath = "session-1/events.jsonl";
    private const string Remote = "https://github.com/example/cross-pc-project.git";

    private readonly TempDirectory _root = new();

    public void Dispose() => _root.Dispose();

    [Fact]
    public void Sync_RestoresRealVisualStudioSessionToOtherProjectPathWithoutPingPong()
    {
        var projectAPath = _root.Combine("pc-a", "project");
        var projectBPath = _root.Combine("pc-b", "different", "project");
        var sessionsA = _root.Combine("pc-a", "session-state");
        var sessionsB = _root.Combine("pc-b", "session-state");
        var syncRoot = _root.Combine("sync");
        Directory.CreateDirectory(projectAPath);
        Directory.CreateDirectory(projectBPath);
        Directory.CreateDirectory(sessionsB);
        Assert.Empty(Directory.EnumerateFileSystemEntries(sessionsB));

        var identity = ProjectIdentity.FromRemote(Remote);
        var projectA = new ProjectInfo { Identity = identity, LocalPath = projectAPath };
        var projectB = new ProjectInfo { Identity = identity, LocalPath = projectBPath };
        var descriptorA = Descriptor(projectAPath);
        var descriptorB = Descriptor(projectBPath);
        var portableDescriptor = Descriptor("${project}");
        var transcript = Encoding.UTF8.GetBytes("{\"type\":\"user.message\",\"text\":\"hello\"}\n");
        var sessionA = Path.Combine(sessionsA, SessionId);
        Directory.CreateDirectory(sessionA);
        File.WriteAllBytes(Path.Combine(sessionA, "workspace.yaml"), descriptorA);
        File.WriteAllBytes(Path.Combine(sessionA, "events.jsonl"), transcript);

        var service = new ChatSyncService(new FakeProcessGuard());
        var stateA = new SyncState();
        var push = service.Sync(new VisualStudioChatProvider(sessionsA), projectA, syncRoot, stateA);
        var syncedSession = Path.Combine(syncRoot, "visualstudio", identity.Slug, SessionId);
        var syncedDescriptor = Path.Combine(syncedSession, "workspace.yaml");
        var syncedTranscript = Path.Combine(syncedSession, "events.jsonl");

        Assert.Equal(2, push.PushedCount);
        Assert.Equal(SyncAction.Pushed, Assert.Single(push.Entries, entry => entry.RelativePath == DescriptorPath).Action);
        Assert.Equal(descriptorA, File.ReadAllBytes(Path.Combine(sessionA, "workspace.yaml")));
        Assert.Equal(portableDescriptor, File.ReadAllBytes(syncedDescriptor));
        Assert.Equal(transcript, File.ReadAllBytes(syncedTranscript));
        Assert.Equal(SyncState.ComputeHash(portableDescriptor), stateA.GetBaseline(DescriptorPath));

        var stateB = new SyncState();
        Assert.Null(stateB.GetBaseline(DescriptorPath));
        var pull = service.Sync(new VisualStudioChatProvider(sessionsB), projectB, syncRoot, stateB);
        var localDescriptor = Path.Combine(sessionsB, SessionId, "workspace.yaml");
        var localTranscript = Path.Combine(sessionsB, SessionId, "events.jsonl");

        Assert.Equal(2, pull.PulledCount);
        Assert.Equal(SyncAction.Pulled, Assert.Single(pull.Entries, entry => entry.RelativePath == DescriptorPath).Action);
        Assert.Equal(descriptorB, File.ReadAllBytes(localDescriptor));
        Assert.Equal(transcript, File.ReadAllBytes(localTranscript));
        Assert.Equal(portableDescriptor, File.ReadAllBytes(syncedDescriptor));
        Assert.Equal(SyncState.ComputeHash(portableDescriptor), stateB.GetBaseline(DescriptorPath));
        Assert.Equal(SyncState.ComputeHash(syncedTranscript), stateB.GetBaseline(TranscriptPath));
        Assert.False(Directory.Exists(ChatSyncService.GetBackupDirectory(syncRoot)));

        var sentinel = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(localDescriptor, sentinel);
        File.SetLastWriteTimeUtc(syncedDescriptor, sentinel);
        var second = service.Sync(new VisualStudioChatProvider(sessionsB), projectB, syncRoot, stateB);

        Assert.Equal(2, second.UnchangedCount);
        Assert.Equal(SyncAction.Unchanged, Assert.Single(second.Entries, entry => entry.RelativePath == DescriptorPath).Action);
        Assert.Equal(descriptorB, File.ReadAllBytes(localDescriptor));
        Assert.Equal(portableDescriptor, File.ReadAllBytes(syncedDescriptor));
        Assert.Equal(sentinel, File.GetLastWriteTimeUtc(localDescriptor));
        Assert.Equal(sentinel, File.GetLastWriteTimeUtc(syncedDescriptor));
        Assert.Equal(SyncState.ComputeHash(portableDescriptor), stateB.GetBaseline(DescriptorPath));
        Assert.False(Directory.Exists(ChatSyncService.GetBackupDirectory(syncRoot)));
    }

    private static byte[] Descriptor(string root) => Encoding.UTF8.GetBytes(
        $"id: {SessionId}\r\ncwd: {root}{Path.DirectorySeparatorChar}\r\ngit_root: {root}\r\nrepository: {Remote}\r\nbranch: main\r\n");
}