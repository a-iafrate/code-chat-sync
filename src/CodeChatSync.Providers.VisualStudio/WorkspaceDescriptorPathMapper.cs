using System.Text;

namespace CodeChatSync.Providers.VisualStudio;

/// <summary>
/// Rewrites the absolute paths in a session's <c>workspace.yaml</c> so a chat restored
/// on another PC is listed under that PC's copy of the project.
/// </summary>
/// <remarks>
/// <para>
/// The Copilot backend Visual Studio hosts matches a solution's chats on the
/// descriptor's <c>cwd</c>, so one still carrying the source PC's path is restored but
/// never shown. Whether that match is case-sensitive is unconfirmed: Visual Studio was
/// observed writing the same folder as both <c>c:\</c> and <c>C:\</c> for sessions it
/// created itself, so the tool deliberately does not normalize the spelling of a path
/// that is already correct.
/// </para>
/// <para>
/// Mapping the path is necessary but not sufficient. A restored session also needs a row
/// in the machine-wide <c>session-store.db</c> before it is listed at all — see
/// <see cref="CopilotSessionStore"/>, which adds it locally without ever copying that
/// file between PCs.
/// </para>
/// <para>
/// In the sync folder, paths under the project root are stored with
/// <see cref="ProjectRootToken"/> in place of the root. This is undocumented,
/// best-effort behavior: only the top-level <c>cwd</c> and <c>git_root</c> lines are
/// touched, and every other byte is preserved.
/// </para>
/// </remarks>
public static class WorkspaceDescriptorPathMapper
{
    /// <summary>Placeholder for the project root in the portable form.</summary>
    public const string ProjectRootToken = "${project}";

    private const string WorkingDirectoryKey = "cwd";
    private const string GitRootKey = "git_root";

    private static readonly byte[] Utf8Preamble = [0xEF, 0xBB, 0xBF];
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Replaces this PC's project root with <see cref="ProjectRootToken"/>.</summary>
    public static byte[] ToPortable(byte[] content, string projectRoot)
    {
        ArgumentNullException.ThrowIfNull(content);
        var root = NormalizeRoot(projectRoot);
        return Rewrite(content, (_, value, _) => TryRelativeTo(value, root, out var rest) ? ProjectRootToken + rest : null);
    }

    /// <summary>
    /// Replaces <see cref="ProjectRootToken"/> with this PC's project root, and maps a
    /// path from another PC onto it.
    /// </summary>
    /// <param name="directoryExists">
    /// Used to recognize another PC's path: an absolute path that exists here is left
    /// alone, since it may be a legitimate location on this PC (such as a worktree).
    /// </param>
    public static byte[] ToLocal(byte[] content, string projectRoot, Func<string, bool> directoryExists)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(directoryExists);
        var root = NormalizeRoot(projectRoot);

        return Rewrite(content, (key, value, values) =>
        {
            if (TryRelativeTo(value, ProjectRootToken, out var rest) || TryRelativeTo(value, root, out rest))
            {
                return root + rest;
            }

            if (!IsForeignPath(value, directoryExists))
            {
                return null;
            }

            // A working directory below the source PC's Git root keeps its subfolder.
            if (key == WorkingDirectoryKey
                && values.TryGetValue(GitRootKey, out var foreignGitRoot)
                && IsForeignPath(foreignGitRoot, directoryExists)
                && TryRelativeTo(value, NormalizeRoot(foreignGitRoot), out rest))
            {
                return root + rest;
            }

            return EndsWithSeparator(value) ? root + Path.DirectorySeparatorChar : root;
        });
    }

    private delegate string? ValueMapper(string key, string value, IReadOnlyDictionary<string, string> values);

    private static byte[] Rewrite(byte[] content, ValueMapper map)
    {
        var hasPreamble = content.AsSpan().StartsWith(Utf8Preamble);
        string text;
        try
        {
            text = StrictUtf8.GetString(content, hasPreamble ? Utf8Preamble.Length : 0, content.Length - (hasPreamble ? Utf8Preamble.Length : 0));
        }
        catch (DecoderFallbackException)
        {
            return content;
        }

        var lines = SplitKeepingTerminators(text);
        var entries = new List<(int Index, string Key, string Value, char? Quote)>();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < lines.Count; index++)
        {
            if (TryParse(lines[index].Content, out var key, out var value, out var quote))
            {
                entries.Add((index, key, value, quote));
                values[key] = value;
            }
        }

        var changed = false;
        foreach (var (index, key, value, quote) in entries)
        {
            var mapped = map(key, value, values);
            if (mapped is null || mapped == value)
            {
                continue;
            }

            lines[index] = (Format(key, mapped, quote), lines[index].Terminator);
            changed = true;
        }

        if (!changed)
        {
            return content;
        }

        var builder = new StringBuilder(text.Length + 64);
        foreach (var (lineContent, terminator) in lines)
        {
            builder.Append(lineContent).Append(terminator);
        }

        var body = StrictUtf8.GetBytes(builder.ToString());
        return hasPreamble ? [.. Utf8Preamble, .. body] : body;
    }

    private static List<(string Content, string Terminator)> SplitKeepingTerminators(string text)
    {
        var lines = new List<(string, string)>();
        var start = 0;
        while (start < text.Length)
        {
            var newline = text.IndexOf('\n', start);
            if (newline < 0)
            {
                lines.Add((text[start..], string.Empty));
                break;
            }

            var contentEnd = newline > start && text[newline - 1] == '\r' ? newline - 1 : newline;
            lines.Add((text[start..contentEnd], text[contentEnd..(newline + 1)]));
            start = newline + 1;
        }

        return lines;
    }

    private static bool TryParse(string line, out string key, out string value, out char? quote)
    {
        key = value = string.Empty;
        quote = null;

        foreach (var candidate in (string[])[WorkingDirectoryKey, GitRootKey])
        {
            if (!line.StartsWith(candidate + ":", StringComparison.Ordinal))
            {
                continue;
            }

            var raw = line[(candidate.Length + 1)..].Trim();
            if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
            {
                quote = '"';
                value = UnescapeDoubleQuoted(raw[1..^1]);
            }
            else if (raw.Length >= 2 && raw[0] == '\'' && raw[^1] == '\'')
            {
                quote = '\'';
                value = raw[1..^1].Replace("''", "'", StringComparison.Ordinal);
            }
            else if (raw.Length == 0 || raw[0] is '"' or '\'' || raw.Contains(" #", StringComparison.Ordinal))
            {
                // Empty, malformed or commented values are left untouched.
                return false;
            }
            else
            {
                value = raw;
            }

            key = candidate;
            return true;
        }

        return false;
    }

    private static string UnescapeDoubleQuoted(string inner)
    {
        var builder = new StringBuilder(inner.Length);
        for (var index = 0; index < inner.Length; index++)
        {
            if (inner[index] != '\\' || index + 1 >= inner.Length)
            {
                builder.Append(inner[index]);
                continue;
            }

            index++;
            builder.Append(inner[index] switch
            {
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                var other => other
            });
        }

        return builder.ToString();
    }

    private static string Format(string key, string value, char? quote)
    {
        if (quote == '\'')
        {
            return $"{key}: '{value.Replace("'", "''", StringComparison.Ordinal)}'";
        }

        if (quote == '"' || NeedsQuoting(value))
        {
            var escaped = value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
            return $"{key}: \"{escaped}\"";
        }

        return $"{key}: {value}";
    }

    private static bool NeedsQuoting(string value) =>
        value.Length == 0
        || char.IsWhiteSpace(value[0])
        || char.IsWhiteSpace(value[^1])
        || "-?:,[]{}#&*!|>'\"%@`".Contains(value[0])
        || value.Contains(": ", StringComparison.Ordinal)
        || value.Contains(" #", StringComparison.Ordinal);

    private static bool TryRelativeTo(string value, string root, out string rest)
    {
        rest = string.Empty;
        if (root.Length == 0 || !value.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (value.Length == root.Length)
        {
            return true;
        }

        if (value[root.Length] is not ('\\' or '/'))
        {
            return false;
        }

        rest = value[root.Length..];
        return true;
    }

    private static bool IsForeignPath(string value, Func<string, bool> directoryExists)
    {
        if (!Path.IsPathFullyQualified(value))
        {
            return false;
        }

        try
        {
            return !directoryExists(value);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static bool EndsWithSeparator(string value) =>
        value.Length > 0 && value[^1] is '\\' or '/';

    private static string NormalizeRoot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return path == ProjectRootToken
            ? path
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
}
