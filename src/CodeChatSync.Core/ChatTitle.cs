namespace CodeChatSync.Core;

/// <summary>Normalizes a title the user typed, before it is written into any chat file.</summary>
public static class ChatTitle
{
    /// <summary>Longest title kept; the lists show one line, and a descriptor holds a single value.</summary>
    public const int MaximumLength = 200;

    /// <summary>
    /// The title as it will be stored, or <see langword="null"/> when nothing usable was typed.
    /// </summary>
    /// <remarks>
    /// Always one line: a name is a single value in a descriptor and a single row in a list, so
    /// line breaks and runs of whitespace become one space, and control characters — which
    /// have no business in a name and can corrupt the file that holds it — are dropped.
    /// </remarks>
    public static string? Normalize(string? typed)
    {
        if (string.IsNullOrWhiteSpace(typed))
        {
            return null;
        }

        var cleaned = new string([.. typed.Select(character =>
            char.IsWhiteSpace(character) ? ' ' : char.IsControl(character) ? '\0' : character)
            .Where(character => character != '\0')]);

        var collapsed = string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (collapsed.Length == 0)
        {
            return null;
        }

        if (collapsed.Length <= MaximumLength)
        {
            return collapsed;
        }

        // Never split a surrogate pair: half of one is not a character.
        var length = char.IsHighSurrogate(collapsed[MaximumLength - 1]) ? MaximumLength - 1 : MaximumLength;
        return collapsed[..length].TrimEnd();
    }
}
