using CodeChatSync.Core;

namespace CodeChatSync.Tests;

public sealed class AutoStartManagerTests
{
    private const string ExecutablePath = @"C:\Program Files\CodeChatSync\CodeChatSync.App.exe";

    [Fact]
    public void IsEnabled_IsFalseWhenNothingIsRegistered()
    {
        var manager = new AutoStartManager(new FakeStartupEntryStore());

        Assert.False(manager.IsEnabled(ExecutablePath));
    }

    [Fact]
    public void Enable_RegistersTheQuotedExecutable()
    {
        var store = new FakeStartupEntryStore();
        var manager = new AutoStartManager(store);

        manager.Enable(ExecutablePath);

        Assert.Equal($"\"{ExecutablePath}\"", store.Entries[AutoStartManager.DefaultEntryName]);
        Assert.True(manager.IsEnabled(ExecutablePath));
    }

    [Fact]
    public void Disable_RemovesTheEntry()
    {
        var store = new FakeStartupEntryStore();
        var manager = new AutoStartManager(store);
        manager.Enable(ExecutablePath);

        manager.Disable();

        Assert.Empty(store.Entries);
        Assert.False(manager.IsEnabled(ExecutablePath));
    }

    [Fact]
    public void Disable_IsSafeWhenNothingIsRegistered()
    {
        var manager = new AutoStartManager(new FakeStartupEntryStore());

        manager.Disable();

        Assert.False(manager.IsEnabled(ExecutablePath));
    }

    [Fact]
    public void Enable_IsIdempotent()
    {
        var store = new FakeStartupEntryStore();
        var manager = new AutoStartManager(store);

        manager.Enable(ExecutablePath);
        manager.Enable(ExecutablePath);

        Assert.Single(store.Entries);
    }

    [Fact]
    public void IsEnabled_IgnoresAnEntryPointingAtAnotherBuild()
    {
        var store = new FakeStartupEntryStore();
        store.Entries[AutoStartManager.DefaultEntryName] = @"""C:\old\CodeChatSync.App.exe""";
        var manager = new AutoStartManager(store);

        Assert.False(manager.IsEnabled(ExecutablePath));
    }

    [Fact]
    public void Enable_RepairsAnEntryLeftByAnotherBuild()
    {
        var store = new FakeStartupEntryStore();
        store.Entries[AutoStartManager.DefaultEntryName] = @"""C:\old\CodeChatSync.App.exe""";
        var manager = new AutoStartManager(store);

        manager.Enable(ExecutablePath);

        Assert.True(manager.IsEnabled(ExecutablePath));
        Assert.Single(store.Entries);
    }

    [Fact]
    public void IsEnabled_AcceptsAnUnquotedEntry()
    {
        var store = new FakeStartupEntryStore();
        store.Entries[AutoStartManager.DefaultEntryName] = ExecutablePath;

        Assert.True(new AutoStartManager(store).IsEnabled(ExecutablePath));
    }

    [Fact]
    public void IsEnabled_IgnoresPathCasing()
    {
        var store = new FakeStartupEntryStore();
        var manager = new AutoStartManager(store);
        manager.Enable(ExecutablePath);

        Assert.True(manager.IsEnabled(ExecutablePath.ToUpperInvariant()));
    }

    [Fact]
    public void IsEnabled_IgnoresAMalformedLeftoverEntry()
    {
        var store = new FakeStartupEntryStore();
        store.Entries[AutoStartManager.DefaultEntryName] = "\"\"";

        Assert.False(new AutoStartManager(store).IsEnabled(ExecutablePath));
    }

    [Fact]
    public void FormatCommand_QuotesAPathContainingSpaces()
    {
        var command = AutoStartManager.FormatCommand(@"C:\Program Files\CodeChatSync\CodeChatSync.App.exe");

        Assert.StartsWith("\"", command);
        Assert.EndsWith("\"", command);
    }

    [Fact]
    public void Set_AppliesAndReportsTheRequestedState()
    {
        var store = new FakeStartupEntryStore();
        var manager = new AutoStartManager(store);

        Assert.True(manager.Set(enabled: true, ExecutablePath));
        Assert.True(manager.IsEnabled(ExecutablePath));

        Assert.False(manager.Set(enabled: false, ExecutablePath));
        Assert.False(manager.IsEnabled(ExecutablePath));
    }

    [Fact]
    public void UsesTheSuppliedEntryName()
    {
        var store = new FakeStartupEntryStore();

        new AutoStartManager(store, "CodeChatSync.Dev").Enable(ExecutablePath);

        Assert.True(store.Entries.ContainsKey("CodeChatSync.Dev"));
        Assert.False(store.Entries.ContainsKey(AutoStartManager.DefaultEntryName));
    }

    [Fact]
    public void RejectsABlankEntryName()
    {
        Assert.Throws<ArgumentException>(() => new AutoStartManager(new FakeStartupEntryStore(), "  "));
    }

    /// <summary>Keeps auto-start tests away from the real Windows registry.</summary>
    private sealed class FakeStartupEntryStore : IStartupEntryStore
    {
        public Dictionary<string, string> Entries { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? Read(string name) => Entries.GetValueOrDefault(name);

        public void Write(string name, string command) => Entries[name] = command;

        public void Remove(string name) => Entries.Remove(name);
    }
}
