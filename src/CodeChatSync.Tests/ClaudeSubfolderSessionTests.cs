using System.Text.Json;
using CodeChatSync.Core;
using CodeChatSync.Providers.Claude;
using CodeChatSync.Tests.TestSupport;

namespace CodeChatSync.Tests;

/// <summary>
/// Claude Code stores a session under the directory it was started in, so one project
/// spreads over as many folders as the subdirectories it was run from. Running it from a
/// repository's <c>src</c> is the common case, and those sessions have to sync too.
/// </summary>
public sealed class ClaudeSubfolderSessionTests : IDisposable
{
    private const string RootSession = "11111111-1111-1111-1111-111111111111";
    private const string SourceSession = "22222222-2222-2222-2222-222222222222";

    private readonly TempDirectory _root = new();
    private readonly string _projectsRoot;
    private readonly string _projectRoot;
    private readonly ProjectInfo _project;

    public ClaudeSubfolderSessionTests()
    {
        _projectsRoot = _root.Combine("claude", "projects");
        _projectRoot = _root.Combine("repo");
        Directory.CreateDirectory(_projectsRoot);
        Directory.CreateDirectory(Path.Combine(_projectRoot, "src"));
        _project = new ProjectInfo
        {
            Identity = ProjectIdentity.FromRemote("https://github.com/example/repo.git"),
            LocalPath = _projectRoot
        };
    }

    public void Dispose() => _root.Dispose();

    [Fact]
    public void Discover_IncludesSessionsStartedInASubfolder()
    {
        WriteTranscript(_projectRoot, RootSession);
        WriteTranscript(Path.Combine(_projectRoot, "src"), SourceSession);

        var locations = Provider().Discover(_project).ToArray();

        Assert.Equal(
            [$"{RootSession}.jsonl", $"src/{SourceSession}.jsonl"],
            locations.Select(location => location.RelativePath).Order());
    }

    /// <summary>
    /// The archived path has to say which directory the session came from, and say it the
    /// same way on every PC, or a restore cannot rebuild the right storage folder.
    /// </summary>
    [Fact]
    public void Discover_ArchivesASubfolderSessionUnderThatSubfolder()
    {
        WriteTranscript(Path.Combine(_projectRoot, "src"), SourceSession);

        var location = Assert.Single(Provider().Discover(_project));

        Assert.Equal($"src/{SourceSession}.jsonl", location.RelativePath);
        Assert.DoesNotContain(_projectRoot, location.RelativePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Discover_LeavesOutSessionsFromOutsideTheProject()
    {
        var elsewhere = _root.Combine("other");
        Directory.CreateDirectory(elsewhere);
        WriteTranscript(elsewhere, SourceSession);

        Assert.Empty(Provider().Discover(_project));
    }

    [Fact]
    public void MapToLocal_SendsASubfolderSessionBackToItsOwnStorageFolder()
    {
        var provider = Provider();

        var target = provider.MapToLocal(_project, $"src/{SourceSession}.jsonl");

        Assert.True(ClaudeProjectFolderName.TryGet(Path.Combine(_projectRoot, "src"), out var expectedFolder));
        Assert.Equal(expectedFolder, Path.GetFileName(Path.GetDirectoryName(target)));
        Assert.Equal($"{SourceSession}.jsonl", Path.GetFileName(target));
    }

    [Fact]
    public void MapToLocal_KeepsARootSessionInTheProjectsOwnFolder()
    {
        var provider = Provider();

        var target = provider.MapToLocal(_project, $"{RootSession}.jsonl");

        Assert.True(ClaudeProjectFolderName.TryGet(_projectRoot, out var expectedFolder));
        Assert.Equal(expectedFolder, Path.GetFileName(Path.GetDirectoryName(target)));
    }

    [Fact]
    public void GetSessionId_ReadsTheSessionFromASubfolderPath()
    {
        Assert.Equal(SourceSession, Provider().GetSessionId($"src/{SourceSession}.jsonl"));
        Assert.Equal(RootSession, Provider().GetSessionId($"{RootSession}.jsonl"));
    }

    [Fact]
    public void GetRestoreRefusal_AcceptsATranscriptRecordedInASubfolder()
    {
        var archived = _root.Combine("archived.jsonl");
        WriteTranscriptFile(archived, Path.Combine(_projectRoot, "src"));

        Assert.Null(Provider().GetRestoreRefusal(_project, archived));
    }

    [Fact]
    public void GetRestoreRefusal_StillRefusesATranscriptFromAnotherProject()
    {
        var archived = _root.Combine("foreign.jsonl");
        WriteTranscriptFile(archived, _root.Combine("other"));

        Assert.NotNull(Provider().GetRestoreRefusal(_project, archived));
    }

    /// <summary>
    /// Registration re-checks the list's choice against the candidates, and the list
    /// offers a repository root while candidates are the folders Claude Code was run
    /// from. Matching them by equality would reject every project run from a subfolder.
    /// </summary>
    [Theory]
    [InlineData("repo", "repo", true)]
    [InlineData("repo/src", "repo", true)]
    [InlineData("repo/src/nested", "repo", true)]
    [InlineData("other", "repo", false)]
    [InlineData("repo-other", "repo", false)]
    public void IsWithinProject_MatchesAFolderAndEverythingBelowIt(string candidate, string root, bool expected)
    {
        var actual = ClaudeProjectDiscovery.IsWithinProject(_root.Combine(candidate.Split('/')), _root.Combine(root));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void IsWithinProject_RefusesPathsItCannotTrust()
    {
        Assert.False(ClaudeProjectDiscovery.IsWithinProject("relative", _projectRoot));
        Assert.False(ClaudeProjectDiscovery.IsWithinProject(_projectRoot, "relative"));
    }

    private ClaudeCodeChatProvider Provider() => new(new FakeProcessGuard(), _projectsRoot);

    private void WriteTranscript(string workingDirectory, string sessionId)
    {
        Assert.True(ClaudeProjectFolderName.TryGet(workingDirectory, out var folderName));
        var folder = Path.Combine(_projectsRoot, folderName);
        Directory.CreateDirectory(folder);
        WriteTranscriptFile(Path.Combine(folder, $"{sessionId}.jsonl"), workingDirectory);
    }

    private static void WriteTranscriptFile(string path, string workingDirectory)
    {
        var line = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["type"] = "user",
            ["cwd"] = workingDirectory,
            ["sessionId"] = Guid.NewGuid().ToString()
        });

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, [line]);
    }
}
