namespace CodeChatSync.Tests.TestSupport;

/// <summary>
/// Groups the tests that read or mutate process-wide environment variables, so they
/// never run concurrently with each other.
/// </summary>
[CollectionDefinition(Name)]
public sealed class EnvironmentCollection
{
    public const string Name = "Environment";
}

/// <summary>Temporary directory removed when the test finishes.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codechatsync-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] segments) =>
        System.IO.Path.Combine([Path, .. segments]);

    public string WriteFile(string relativePath, string content)
    {
        var fullPath = Combine(relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        var directory = System.IO.Path.GetDirectoryName(fullPath);
        if (directory is { Length: > 0 })
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(fullPath, content);
        return fullPath;
    }

    public void Dispose()
    {
        try
        {
            if (!Directory.Exists(Path))
            {
                return;
            }

            ClearReadOnlyAttributes(Path);
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A leftover temp folder must not fail the test run.
        }
    }

    /// <summary>
    /// Git stores its objects as read-only files, which blocks a recursive delete.
    /// </summary>
    private static void ClearReadOnlyAttributes(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(file);
            if (attributes.HasFlag(FileAttributes.ReadOnly))
            {
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
            }
        }
    }
}
