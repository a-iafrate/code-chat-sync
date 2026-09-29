using CodeChatSync.Core;

namespace CodeChatSync.Tests.TestSupport;

/// <summary>
/// Provider whose chat files live directly under the project's local path, so tests
/// can exercise <see cref="ChatSyncService"/> without touching real tool data.
/// </summary>
internal sealed class FakeChatProvider(string localRoot, params string[] processNames) : IChatProvider
{
    public string Id { get; init; } = "fake";

    public IReadOnlyList<string> ProcessNames { get; } = processNames;

    /// <summary>Relative paths the provider should report as currently open.</summary>
    public HashSet<string> InUseRelativePaths { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<ChatLocation> Discover(ProjectInfo project)
    {
        if (!Directory.Exists(localRoot))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(localRoot, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(file);
            var relativePath = Path.GetRelativePath(localRoot, file).Replace('\\', '/');
            yield return new ChatLocation
            {
                RelativePath = relativePath,
                LocalPath = file,
                Length = info.Length,
                LastWriteTimeUtc = info.LastWriteTimeUtc,
                IsInUse = InUseRelativePaths.Contains(relativePath)
            };
        }
    }

    public string MapToLocal(ProjectInfo project, string relativePath) =>
        RelativePathGuard.ResolveUnder(localRoot, relativePath);
}
