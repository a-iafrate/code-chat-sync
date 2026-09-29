namespace CodeChatSync.Providers.Claude;

/// <summary>
/// Derives the folder name Claude Code uses under its <c>projects</c> directory for a
/// working directory.
/// </summary>
/// <remarks>
/// Observed, undocumented behavior: every character outside <c>[A-Za-z0-9]</c> becomes
/// <c>-</c>, so <c>C:\src\app</c> is stored as <c>C--src-app</c>. The mapping is lossy
/// (<c>C:\a-b</c> and <c>C:\a\b</c> share a folder), so the name is only used to decide
/// where to store files, never to identify a project. Longer names are shortened by
/// Claude Code with a scheme that is not reproduced here, so they are refused.
/// </remarks>
public static class ClaudeProjectFolderName
{
    /// <summary>Longest folder name produced without Claude Code's shortening scheme.</summary>
    public const int MaxLength = 200;

    /// <summary>
    /// Returns the storage folder name for <paramref name="absolutePath"/>, or
    /// <see langword="false"/> when the path is not fully qualified or too long.
    /// </summary>
    public static bool TryGet(string? absolutePath, out string folderName)
    {
        folderName = string.Empty;
        if (!LocalPaths.TryNormalize(absolutePath, out var normalized))
        {
            return false;
        }

        var characters = normalized.ToCharArray();
        for (var index = 0; index < characters.Length; index++)
        {
            if (!char.IsAsciiLetterOrDigit(characters[index]))
            {
                characters[index] = '-';
            }
        }

        if (characters.Length > MaxLength)
        {
            return false;
        }

        folderName = new string(characters);
        return true;
    }

    /// <summary>Compares folder names the way the local file system does.</summary>
    internal static bool AreSame(string left, string right) =>
        string.Equals(left, right, LocalPaths.Comparison);
}
