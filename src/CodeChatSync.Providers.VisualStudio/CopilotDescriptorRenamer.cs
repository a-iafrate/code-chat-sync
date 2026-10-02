using System.Globalization;
using System.Text;
using CodeChatSync.Core;

namespace CodeChatSync.Providers.VisualStudio;

/// <summary>
/// Renames a session the way Visual Studio does: by rewriting <c>name</c> and setting
/// <c>user_named: true</c> in the session's <c>workspace.yaml</c>.
/// </summary>
/// <remarks>
/// Every other line, and the file's line endings, are kept exactly as they were. The
/// descriptor is backed up first, because a rename rewrites a file the conversation's
/// identity depends on.
/// </remarks>
internal static class CopilotDescriptorRenamer
{
    /// <summary>
    /// Rewrites the descriptor in <paramref name="sessionDirectory"/>. Returns
    /// <see langword="false"/> when there is no descriptor to rewrite.
    /// </summary>
    public static bool Rename(string sessionDirectory, string title, string? backupDirectory = null)
    {
        var path = Path.Combine(sessionDirectory, CopilotChatDiscovery.WorkspaceDescriptorFileName);
        if (!File.Exists(path))
        {
            return false;
        }

        var original = File.ReadAllBytes(path);
        var hasBom = original.AsSpan().StartsWith(Encoding.UTF8.Preamble);
        var text = new UTF8Encoding(false).GetString(original, hasBom ? Encoding.UTF8.Preamble.Length : 0, original.Length - (hasBom ? Encoding.UTF8.Preamble.Length : 0));
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        var lines = text.Split(newline).ToList();
        var nameLine = $"name: {Quote(title)}";
        var namedLine = "user_named: true";
        var nameDone = false;
        var namedDone = false;

        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            if (IsKey(line, "name"))
            {
                lines[index] = nameLine;
                nameDone = true;
            }
            else if (IsKey(line, "user_named"))
            {
                lines[index] = namedLine;
                namedDone = true;
            }
        }

        // Keep a trailing newline a line apart from the keys that are added.
        var insertAt = lines.Count > 0 && lines[^1].Length == 0 ? lines.Count - 1 : lines.Count;
        if (!nameDone)
        {
            lines.Insert(insertAt++, nameLine);
        }

        if (!namedDone)
        {
            lines.Insert(insertAt, namedLine);
        }

        BackUp(path, backupDirectory);

        var encoding = new UTF8Encoding(hasBom);
        var rewritten = encoding.GetPreamble().Concat(encoding.GetBytes(string.Join(newline, lines))).ToArray();

        // Written next to the target and swapped in, so a crash never leaves half a descriptor.
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, rewritten);
        File.Move(temporary, path, overwrite: true);
        return true;
    }

    private static bool IsKey(string line, string key) =>
        line.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase);

    /// <summary>A double-quoted scalar, using the escapes the descriptor reader decodes.</summary>
    internal static string Quote(string value)
    {
        var builder = new StringBuilder(value.Length + 2).Append('"');
        foreach (var character in value)
        {
            builder.Append(character switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ => character.ToString()
            });
        }

        return builder.Append('"').ToString();
    }

    private static void BackUp(string path, string? backupDirectory)
    {
        var directory = Path.Combine(
            backupDirectory ?? Path.Combine(LocalConfig.GetDefaultBackupDirectory(), "visualstudio-titles"),
            DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture),
            Path.GetFileName(Path.GetDirectoryName(path)) ?? "session");
        Directory.CreateDirectory(directory);
        File.Copy(path, Path.Combine(directory, Path.GetFileName(path)), overwrite: false);
    }
}
