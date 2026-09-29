using CodeChatSync.Core;
using CodeChatSync.Tests.TestSupport;

namespace CodeChatSync.Tests;

public class SyncStateTests
{
    [Fact]
    public void ComputeHash_ReturnsNullForMissingFile()
    {
        using var root = new TempDirectory();

        Assert.Null(SyncState.ComputeHash(root.Combine("missing.jsonl")));
    }

    [Fact]
    public void ComputeHash_MatchesForIdenticalContentAndDiffersOtherwise()
    {
        using var root = new TempDirectory();
        var first = root.WriteFile("a.jsonl", "same");
        var second = root.WriteFile("b.jsonl", "same");
        var third = root.WriteFile("c.jsonl", "different");

        Assert.Equal(SyncState.ComputeHash(first), SyncState.ComputeHash(second));
        Assert.NotEqual(SyncState.ComputeHash(first), SyncState.ComputeHash(third));
    }

    [Fact]
    public void GetBaseline_IsIndependentOfPathSeparatorAndCase()
    {
        var state = new SyncState();
        state.SetBaseline("session/events.jsonl", "HASH");

        Assert.Equal("HASH", state.GetBaseline("session\\events.jsonl"));
        Assert.Equal("HASH", state.GetBaseline("SESSION/EVENTS.JSONL"));
    }

    [Fact]
    public void Remove_ClearsTheBaseline()
    {
        var state = new SyncState();
        state.SetBaseline("session/events.jsonl", "HASH");

        state.Remove("session/events.jsonl");

        Assert.Null(state.GetBaseline("session/events.jsonl"));
    }

    [Fact]
    public void SaveAndLoad_RoundTripsBaselines()
    {
        using var root = new TempDirectory();
        var statePath = root.Combine("state", "sync-state.json");
        var state = new SyncState();
        state.SetBaseline("session/events.jsonl", "HASH");
        state.Save(statePath);

        var reloaded = SyncState.Load(statePath);

        Assert.Equal("HASH", reloaded.GetBaseline("session/events.jsonl"));
    }

    [Fact]
    public void Load_ReturnsEmptyStateForMissingFile()
    {
        using var root = new TempDirectory();

        var state = SyncState.Load(root.Combine("absent.json"));

        Assert.Null(state.GetBaseline("session/events.jsonl"));
    }

    [Fact]
    public void Load_ReturnsEmptyStateForCorruptFile()
    {
        using var root = new TempDirectory();
        var statePath = root.WriteFile("sync-state.json", "{ not json");

        var state = SyncState.Load(statePath);

        Assert.Null(state.GetBaseline("session/events.jsonl"));
    }
}
