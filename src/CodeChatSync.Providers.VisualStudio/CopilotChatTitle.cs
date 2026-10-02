namespace CodeChatSync.Providers.VisualStudio;

/// <summary>Turns a session descriptor's <c>name</c> into something worth showing as a title.</summary>
/// <remarks>
/// <para>
/// Visual Studio stores the whole first message as the session name, up to about 500
/// characters, and prefixes it with an <c>&lt;ide_context&gt;</c> block describing the open
/// solution — which is not what the user typed and not what the chat history shows.
/// </para>
/// <para>
/// When that block is not closed, the name was cut off inside it and nothing the user wrote
/// survives, so there is no honest title: <see langword="null"/> is returned rather than a
/// title made of the preamble.
/// </para>
/// </remarks>
internal static class CopilotChatTitle
{
    private const string IdeContextOpen = "<ide_context>";
    private const string IdeContextClose = "</ide_context>";

    /// <summary>Longest title kept; the list shows one line.</summary>
    public const int MaximumLength = 120;

    /// <summary>
    /// The message with the <c>&lt;ide_context&gt;</c> block removed, for showing a whole
    /// message rather than a one-line title.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="Clean"/>, a block that is not closed leaves the text exactly as it
    /// is: a full transcript is never cut off, so an unclosed block there is the user's own
    /// text, and removing it would throw their words away.
    /// </remarks>
    public static string StripIdeContext(string text)
    {
        var trimmed = text.TrimStart();
        if (!trimmed.StartsWith(IdeContextOpen, StringComparison.Ordinal))
        {
            return text;
        }

        var end = trimmed.IndexOf(IdeContextClose, StringComparison.Ordinal);
        return end < 0 ? text : trimmed[(end + IdeContextClose.Length)..];
    }

    public static string? Clean(string? raw, int maximumLength = MaximumLength)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var text = raw.TrimStart();
        if (text.StartsWith(IdeContextOpen, StringComparison.Ordinal))
        {
            var end = text.IndexOf(IdeContextClose, StringComparison.Ordinal);
            if (end < 0)
            {
                return null;
            }

            text = text[(end + IdeContextClose.Length)..];
        }

        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (collapsed.Length == 0)
        {
            return null;
        }

        return collapsed.Length <= maximumLength ? collapsed : collapsed[..(maximumLength - 1)] + "…";
    }
}
