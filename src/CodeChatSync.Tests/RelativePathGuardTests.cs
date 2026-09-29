using CodeChatSync.Core;
using CodeChatSync.Tests.TestSupport;

namespace CodeChatSync.Tests;

public class RelativePathGuardTests
{
    [Theory]
    [InlineData("session/events.jsonl", "session/events.jsonl")]
    [InlineData("session\\events.jsonl", "session/events.jsonl")]
    [InlineData("session/events.jsonl/", "session/events.jsonl")]
    [InlineData("./session/./events.jsonl", "session/events.jsonl")]
    [InlineData("session//events.jsonl", "session/events.jsonl")]
    public void Normalize_UnifiesSeparatorsAndTrimsNoiseSegments(string input, string expected)
    {
        Assert.Equal(expected, RelativePathGuard.Normalize(input));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("session/../../outside.txt")]
    [InlineData("session\\..\\..\\outside.txt")]
    public void Normalize_RejectsUpwardTraversal(string input)
    {
        Assert.Throws<ArgumentException>(() => RelativePathGuard.Normalize(input));
    }

    [Theory]
    [InlineData("C:\\Windows\\system.ini")]
    [InlineData("C:/Windows/system.ini")]
    [InlineData("\\\\server\\share\\file.txt")]
    [InlineData("/session/events.jsonl")]
    public void Normalize_RejectsRootedPaths(string input)
    {
        Assert.Throws<ArgumentException>(() => RelativePathGuard.Normalize(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")]
    public void Normalize_RejectsEmptyPaths(string input)
    {
        Assert.Throws<ArgumentException>(() => RelativePathGuard.Normalize(input));
    }

    [Fact]
    public void ResolveUnder_ReturnsPathInsideRoot()
    {
        using var root = new TempDirectory();

        var resolved = RelativePathGuard.ResolveUnder(root.Path, "session/events.jsonl");

        Assert.Equal(root.Combine("session", "events.jsonl"), resolved);
    }

    [Fact]
    public void ResolveUnder_RejectsPathEscapingTheRoot()
    {
        using var root = new TempDirectory();

        Assert.Throws<ArgumentException>(() => RelativePathGuard.ResolveUnder(root.Path, "../escaped.txt"));
    }

    [Fact]
    public void ResolveUnder_RejectsMissingRoot()
    {
        Assert.Throws<ArgumentException>(() => RelativePathGuard.ResolveUnder(" ", "session/events.jsonl"));
    }
}
