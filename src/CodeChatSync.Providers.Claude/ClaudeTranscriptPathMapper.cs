using System.Text;

namespace CodeChatSync.Providers.Claude;

/// <summary>
/// Rewrites the absolute paths a Claude Code transcript records so a chat restored on
/// another PC refers to that PC's own copy of the project.
/// </summary>
/// <remarks>
/// <para>
/// A transcript is JSON Lines, and the project's absolute path is not confined to one
/// field: <c>cwd</c> is recorded on most lines, but so is
/// <c>attachment.snapshot.workingDirectory</c>, tool parameters such as <c>file_path</c>
/// and <c>path</c> (duplicated again under <c>wireToolInputs</c>), and free text inside
/// system-reminder attachments and the assistant's own prose. Measured on a real session:
/// 78% of its lines mentioned the path somewhere. <c>cwd</c> itself is not even constant
/// within one session — it tracks the shell's current directory as the agent moves
/// around, including into subfolders of the project.
/// </para>
/// <para>
/// Extracting and rewriting only the fields with a known name would miss the free-text
/// occurrences, which a tool call's own explanation can easily contain. This instead
/// treats the whole file as text and replaces every occurrence of the project's root —
/// wherever it appears, whatever field or prose it sits in — with a portable token, the
/// same way a human would with a careful find-and-replace. Paths outside the project
/// root (a temp folder the agent happened to use, a different repository) never match
/// and are left alone.
/// </para>
/// <para>
/// A path survives JSON string escaping as every backslash doubled, so the search and
/// replacement both work on that doubled form; nothing here parses the JSON structure.
/// Matching is case-insensitive, since Windows paths are, and a match is only accepted
/// when immediately followed by the end of the path segment (a quote, another escaped
/// backslash, or a forward slash) — otherwise <c>C:\repo</c> would also match inside
/// <c>C:\repo-backup</c>.
/// </para>
/// </remarks>
public static class ClaudeTranscriptPathMapper
{
    /// <summary>
    /// Placeholder for the project root in the portable form.
    /// </summary>
    /// <remarks>
    /// Deliberately not a short, conventional-looking placeholder such as
    /// <c>${project}</c>: a transcript can contain literal source code, and a tool call
    /// that writes code mentioning a common interpolation syntax (shell, JavaScript,
    /// templates) would have that text mistaken for the marker on restore, turning plain
    /// source text into a fabricated path in the chat's history. That exact collision was
    /// found by testing against a real transcript of this project's own development,
    /// where a test fixture literally contained <c>${project}</c> as a string constant.
    /// The GUID suffix makes an unrelated, organic occurrence of the full token
    /// astronomically unlikely. The remaining case — this tool's own source code, which
    /// must define this constant as a string literal, appearing in a transcript of
    /// someone using Claude Code to develop CodeChatSync itself — is not eliminated by any
    /// choice of token, but is harmless: only the historical transcript text would show a
    /// wrong path in that one old message, never a file actually on disk.
    /// </remarks>
    public const string ProjectRootToken = "codechatsync-project-root-7f3b9a2e6d414c8fb5a09e2d7c4f1a83";

    private static readonly byte[] Utf8Preamble = [0xEF, 0xBB, 0xBF];
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Replaces this PC's project root with <see cref="ProjectRootToken"/>.</summary>
    public static byte[] ToPortable(byte[] content, string projectRoot)
    {
        ArgumentNullException.ThrowIfNull(content);
        var needle = JsonEscape(NormalizeRoot(projectRoot));
        return Rewrite(content, needle, ProjectRootToken);
    }

    /// <summary>Replaces <see cref="ProjectRootToken"/> with this PC's project root.</summary>
    public static byte[] ToLocal(byte[] content, string projectRoot)
    {
        ArgumentNullException.ThrowIfNull(content);
        var replacement = JsonEscape(NormalizeRoot(projectRoot));
        return Rewrite(content, ProjectRootToken, replacement);
    }

    /// <summary>
    /// Whether the content already carries <see cref="ProjectRootToken"/>, meaning a push
    /// from some PC has already portabilized it and it is safe to restore anywhere.
    /// </summary>
    public static bool IsPortable(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return TryDecode(content, out var text, out _) && text.Contains(ProjectRootToken, StringComparison.Ordinal);
    }

    private static byte[] Rewrite(byte[] content, string needle, string replacement)
    {
        if (!TryDecode(content, out var text, out var hasPreamble))
        {
            return content;
        }

        var builder = new StringBuilder(text.Length);
        var searchStart = 0;
        var changed = false;

        while (true)
        {
            var index = text.IndexOf(needle, searchStart, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                builder.Append(text, searchStart, text.Length - searchStart);
                break;
            }

            if (!IsSegmentBoundary(text, index + needle.Length))
            {
                // Looks like a match, but continues into a different, longer path segment
                // (for example `C:\repo-backup` while looking for `C:\repo`).
                builder.Append(text, searchStart, index + 1 - searchStart);
                searchStart = index + 1;
                continue;
            }

            builder.Append(text, searchStart, index - searchStart);
            builder.Append(replacement);
            changed = true;
            searchStart = index + needle.Length;
        }

        if (!changed)
        {
            return content;
        }

        var body = StrictUtf8.GetBytes(builder.ToString());
        return hasPreamble ? [.. Utf8Preamble, .. body] : body;
    }

    private static bool TryDecode(byte[] content, out string text, out bool hasPreamble)
    {
        hasPreamble = content.AsSpan().StartsWith(Utf8Preamble);
        try
        {
            text = StrictUtf8.GetString(content, hasPreamble ? Utf8Preamble.Length : 0, content.Length - (hasPreamble ? Utf8Preamble.Length : 0));
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }
    }

    /// <summary>
    /// A match is only a full path segment when what follows ends the JSON string, starts
    /// another escaped separator, or is a plain forward slash (seen in a few tool inputs
    /// that normalize paths).
    /// </summary>
    private static bool IsSegmentBoundary(string text, int index)
    {
        if (index >= text.Length)
        {
            return true;
        }

        var next = text[index];
        if (next is '"' or '/')
        {
            return true;
        }

        // An escaped backslash (`\\` in the JSON text) starting the next segment.
        return next == '\\' && index + 1 < text.Length && text[index + 1] == '\\';
    }

    /// <summary>Doubles every backslash, mirroring how JSON escapes a Windows path.</summary>
    private static string JsonEscape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal);

    private static string NormalizeRoot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
}
