namespace CodeChatSync.Cli;

/// <summary>Shared console formatting helpers.</summary>
internal static class Output
{
    internal static string Format(DateTimeOffset? timestamp) =>
        timestamp?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "(unknown)";

    /// <summary>Collapses a generated title into a single readable console line.</summary>
    internal static string Summarize(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "(none recorded)";
        }

        var firstLine = title.ReplaceLineEndings(" ").Trim();
        const int maxLength = 90;
        return firstLine.Length <= maxLength ? firstLine : firstLine[..maxLength] + "...";
    }
}
