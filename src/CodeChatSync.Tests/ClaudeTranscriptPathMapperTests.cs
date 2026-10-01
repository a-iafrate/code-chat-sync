using System.Text;
using CodeChatSync.Providers.Claude;

namespace CodeChatSync.Tests;

/// <summary>
/// Covers rewriting a Claude Code transcript's absolute paths, so a chat restored on
/// another PC refers to that PC's own copy of the project rather than the one that wrote
/// it.
/// </summary>
public sealed class ClaudeTranscriptPathMapperTests
{
    private const string Root = @"C:\progetti\code-chat-sync";
    private const string OtherRoot = @"D:\clients\erp";
    private const string Token = ClaudeTranscriptPathMapper.ProjectRootToken;

    [Fact]
    public void ToPortable_ReplacesTheCwdFieldWithTheToken()
    {
        var local = Line("""{"type":"user","cwd":"C:\\progetti\\code-chat-sync"}""");

        var portable = ToPortable(local);

        Assert.Equal(Line($$"""{"type":"user","cwd":"{{Token}}"}"""), portable);
    }

    /// <summary>
    /// `cwd` tracks the shell's current directory as the agent moves around, so a
    /// subfolder of the project has to be rewritten too, keeping its own suffix.
    /// </summary>
    [Fact]
    public void ToPortable_KeepsTheSubfolderWhenCwdIsBelowTheProjectRoot()
    {
        var local = Line("""{"cwd":"C:\\progetti\\code-chat-sync\\src\\CodeChatSync.Core"}""");

        var portable = ToPortable(local);

        Assert.Equal(Line($$"""{"cwd":"{{Token}}\\src\\CodeChatSync.Core"}"""), portable);
    }

    /// <summary>
    /// The same path is duplicated across unrelated fields on one line: a tool's own
    /// parameters, the same parameters mirrored elsewhere, and a path-shaped attachment
    /// field. All of them have to move together.
    /// </summary>
    [Fact]
    public void ToPortable_RewritesEveryFieldThatCarriesThePath()
    {
        var local = Line(
            """{"message":{"content":[{"input":{"file_path":"C:\\progetti\\code-chat-sync\\docs\\ARCHITECTURE.md"}}]},"wireToolInputs":{"toolu_1":{"file_path":"C:\\progetti\\code-chat-sync\\docs\\ARCHITECTURE.md"}},"attachment":{"snapshot":{"workingDirectory":"C:\\progetti\\code-chat-sync"}}}""");

        var portable = ToPortable(local);
        var text = Encoding.UTF8.GetString(portable);

        Assert.Equal(3, Occurrences(text, Token));
        Assert.DoesNotContain("progetti", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The path also turns up in the assistant's own prose and in system-reminder
    /// attachment text, not only in dedicated path fields.
    /// </summary>
    [Fact]
    public void ToPortable_RewritesThePathInsideFreeText()
    {
        var local = Line(
            """{"rendered":[{"content":"<system-reminder>\nPrimary working directory: C:\\progetti\\code-chat-sync\\src\n</system-reminder>"}]}""");

        var portable = ToPortable(local);

        Assert.Contains(Token, Encoding.UTF8.GetString(portable), StringComparison.Ordinal);
        Assert.DoesNotContain("progetti", Encoding.UTF8.GetString(portable), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Claude Code was observed recording the same folder with different drive-letter case.</summary>
    [Fact]
    public void ToPortable_MatchesTheRootRegardlessOfCase()
    {
        var local = Line("""{"cwd":"c:\\progetti\\code-chat-sync"}""");

        var portable = ToPortable(local);

        Assert.Equal(Line($$"""{"cwd":"{{Token}}"}"""), portable);
    }

    /// <summary>A sibling folder that merely starts with the same characters must not match.</summary>
    [Fact]
    public void ToPortable_DoesNotMatchALongerSiblingPath()
    {
        var local = Line("""{"cwd":"C:\\progetti\\code-chat-sync-backup"}""");

        var portable = ToPortable(local);

        Assert.Equal(local, portable);
    }

    /// <summary>A path outside the project — a temp folder the agent happened to use — is left alone.</summary>
    [Fact]
    public void ToPortable_LeavesPathsOutsideTheProjectUntouched()
    {
        var local = Line("""{"cwd":"C:\\Users\\aless\\AppData\\Local\\Temp\\scratch"}""");

        Assert.Equal(local, ToPortable(local));
    }

    /// <summary>A few tool inputs were observed normalizing to forward slashes.</summary>
    [Fact]
    public void ToPortable_RecognizesAForwardSlashAsEndingTheSegment()
    {
        var local = Line("""{"path":"C:\\progetti\\code-chat-sync/docs"}""");

        var portable = ToPortable(local);

        Assert.Equal(Line($$"""{"path":"{{Token}}/docs"}"""), portable);
    }

    [Fact]
    public void ToPortable_ReturnsTheInputUnchangedWhenThereIsNothingToMap()
    {
        var local = Line("""{"type":"mode","mode":"default"}""");

        Assert.Same(local, ToPortable(local));
    }

    [Fact]
    public void ToLocal_RestoresTheRootAndItsSubfolder()
    {
        var portable = Line($$"""{"cwd":"{{Token}}\\src"}""");

        var local = ClaudeTranscriptPathMapper.ToLocal(portable, Root);

        Assert.Equal(Line("""{"cwd":"C:\\progetti\\code-chat-sync\\src"}"""), local);
    }

    [Fact]
    public void ToLocal_ReturnsTheInputUnchangedWhenThereIsNoTokenToReplace()
    {
        var content = Line("""{"type":"mode"}""");

        Assert.Same(content, ClaudeTranscriptPathMapper.ToLocal(content, Root));
    }

    [Fact]
    public void RoundTrip_RestoresTheOriginalContentOnTheSamePc()
    {
        var original = Line(
            """{"cwd":"C:\\progetti\\code-chat-sync\\src","message":"edited C:\\progetti\\code-chat-sync\\README.md"}""");

        var restored = ClaudeTranscriptPathMapper.ToLocal(ToPortable(original), Root);

        Assert.Equal(original, restored);
    }

    /// <summary>
    /// Restoring onto a different PC lands on that PC's own path, including when it sits
    /// at a different drive letter or folder name entirely.
    /// </summary>
    [Fact]
    public void ToLocal_UsesWhicheverProjectRootThisPcHas()
    {
        var portable = Line($$"""{"cwd":"{{Token}}"}""");

        var local = ClaudeTranscriptPathMapper.ToLocal(portable, OtherRoot);

        Assert.Equal(Line("""{"cwd":"D:\\clients\\erp"}"""), local);
    }

    /// <summary>
    /// A transcript can embed another file's content verbatim — for example dumping a
    /// config file while investigating it — which can itself mix the casing of the very
    /// same path. Observed directly in a real transcript: a Visual Studio descriptor's
    /// <c>cwd: c:\...</c> and <c>git_root: C:\...</c> ended up quoted side by side after a
    /// tool read that file. Case at each individual occurrence is not preserved through
    /// the portable form — every occurrence restores with this PC's one canonical casing —
    /// so the round trip is not byte-exact here. This is accepted rather than chased,
    /// because <c>ChatSyncService</c> already treats a result that differs only by ASCII
    /// letter case as unchanged, so it never overwrites a local file over this.
    /// </summary>
    [Fact]
    public void RoundTrip_NormalizesCaseWhenTheSameOccurrenceWasRecordedWithBothCasesOnOnePc()
    {
        var original = Line(
            """{"text":"cwd: c:\\progetti\\code-chat-sync\\ and also git_root: C:\\progetti\\code-chat-sync"}""");

        var restored = ClaudeTranscriptPathMapper.ToLocal(ToPortable(original), Root);

        Assert.NotEqual(Encoding.UTF8.GetString(original), Encoding.UTF8.GetString(restored));
        Assert.True(EqualsIgnoringAsciiCase(original, restored));
    }

    private static bool EqualsIgnoringAsciiCase(byte[] left, byte[] right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (var index = 0; index < left.Length; index++)
        {
            var a = left[index] is >= (byte)'A' and <= (byte)'Z' ? (byte)(left[index] + 32) : left[index];
            var b = right[index] is >= (byte)'A' and <= (byte)'Z' ? (byte)(right[index] + 32) : right[index];
            if (a != b)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A short, conventional-looking placeholder would risk exactly this: a tool call
    /// that writes source code mentioning the same interpolation syntax (shell,
    /// JavaScript, templates) gets its plain text mistaken for the marker on restore.
    /// This was found against a real transcript of this project's own development, where
    /// a test fixture literally contained the string <c>${project}</c>. The token is
    /// GUID-qualified specifically so unrelated text cannot organically match it.
    /// </summary>
    [Fact]
    public void ToLocal_DoesNotTreatACommonInterpolationPlaceholderAsTheMarker()
    {
        var portable = Line("""{"text":"Encoding.UTF8.GetBytes(\"cwd: ${project}\\n\")"}""");

        Assert.Equal(portable, ClaudeTranscriptPathMapper.ToLocal(portable, Root));
    }

    [Fact]
    public void IsPortable_RecognizesContentAlreadyMapped()
    {
        var portable = ToPortable(Line("""{"cwd":"C:\\progetti\\code-chat-sync"}"""));

        Assert.True(ClaudeTranscriptPathMapper.IsPortable(portable));
    }

    [Fact]
    public void IsPortable_IsFalseForARawLocalTranscript()
    {
        Assert.False(ClaudeTranscriptPathMapper.IsPortable(Line("""{"cwd":"C:\\progetti\\code-chat-sync"}""")));
    }

    [Fact]
    public void ToPortable_PreservesAByteOrderMark()
    {
        var withBom = Prepend(Utf8Bom, Line("""{"cwd":"C:\\progetti\\code-chat-sync"}"""));

        var portable = ToPortable(withBom);

        Assert.True(portable.AsSpan().StartsWith(Utf8Bom));
        Assert.Equal(Prepend(Utf8Bom, Line($$"""{"cwd":"{{Token}}"}""")), portable);
    }

    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    private static byte[] ToPortable(byte[] content) => ClaudeTranscriptPathMapper.ToPortable(content, Root);

    private static byte[] Line(string json) => Encoding.UTF8.GetBytes(json + "\n");

    private static byte[] Prepend(byte[] prefix, byte[] rest) => [.. prefix, .. rest];

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
