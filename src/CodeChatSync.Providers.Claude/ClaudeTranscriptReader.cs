using System.Text;
using System.Text.Json;

namespace CodeChatSync.Providers.Claude;

/// <summary>
/// Extracts the few metadata fields the provider needs from a Claude Code transcript,
/// without keeping any message content.
/// </summary>
/// <remarks>
/// Transcripts are JSON Lines. Only these top-level string properties are read:
/// <c>cwd</c> (first value, the session's working directory) and <c>aiTitle</c> (last
/// value, the generated title). Every other property is skipped without being
/// materialized, and malformed lines are ignored.
/// </remarks>
internal static class ClaudeTranscriptReader
{
    /// <summary>Lines longer than this are skipped: metadata records are small.</summary>
    private const int MaxParsedLineLength = 1024 * 1024;

    private const int MaxTitleLength = 200;

    private static readonly byte[] CwdProperty = "cwd"u8.ToArray();
    private static readonly byte[] TitleProperty = "aiTitle"u8.ToArray();

    public static TranscriptMetadata Read(string path, bool includeTitle)
    {
        string? workingDirectory = null;
        string? title = null;

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line.Length > MaxParsedLineLength)
            {
                continue;
            }

            var (lineCwd, lineTitle) = ParseLine(line);
            workingDirectory ??= lineCwd;
            if (lineTitle is not null)
            {
                title = lineTitle;
            }

            if (!includeTitle && workingDirectory is not null)
            {
                break;
            }
        }

        return new TranscriptMetadata(workingDirectory, includeTitle ? title : null);
    }

    private static (string? Cwd, string? Title) ParseLine(string line)
    {
        string? cwd = null;
        string? title = null;

        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(line));
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return (null, null);
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var isCwd = reader.ValueTextEquals(CwdProperty);
                var isTitle = !isCwd && reader.ValueTextEquals(TitleProperty);

                if (!reader.Read())
                {
                    break;
                }

                if (reader.TokenType == JsonTokenType.String && (isCwd || isTitle))
                {
                    var value = reader.GetString();
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        continue;
                    }

                    if (isCwd)
                    {
                        cwd ??= value;
                    }
                    else
                    {
                        value = value.Trim();
                        title = value.Length > MaxTitleLength ? value[..MaxTitleLength] : value;
                    }
                }
                else
                {
                    reader.Skip();
                }
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            // A torn or non-JSON line carries no trustworthy metadata.
            return (null, null);
        }

        return (cwd, title);
    }
}

internal sealed record TranscriptMetadata(string? WorkingDirectory, string? Title);
