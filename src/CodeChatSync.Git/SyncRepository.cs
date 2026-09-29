namespace CodeChatSync.Git;

/// <summary>State of the sync repository relevant to a sync run.</summary>
public sealed record SyncRepositoryStatus
{
    public required bool IsGitRepository { get; init; }

    public required bool HasRemote { get; init; }

    /// <summary>Paths with uncommitted changes, relative to the repository root.</summary>
    public required IReadOnlyList<string> PendingChanges { get; init; }

    public string? CurrentBranch { get; init; }

    public bool HasPendingChanges => PendingChanges.Count > 0;
}

/// <summary>How a pull of the sync repository ended.</summary>
public enum PullStatus
{
    /// <summary>The repository has no remote, so this PC works on its own.</summary>
    NoRemote,

    /// <summary>The branch has never been pushed, so there is nothing to pull yet.</summary>
    NoUpstream,

    /// <summary>The local branch now matches the remote.</summary>
    UpToDate,

    /// <summary>The pull could not be completed and nothing was changed locally.</summary>
    Failed
}

/// <summary>Result of pulling the sync repository.</summary>
public sealed record PullOutcome(PullStatus Status, string Message)
{
    /// <summary>Whether the sync can continue: only a real failure stops it.</summary>
    public bool CanContinue => Status is not PullStatus.Failed;
}

/// <summary>
/// Git operations on the user's private sync repository.
/// </summary>
/// <remarks>
/// Only the sync repository is ever touched: client repositories are never read
/// from or written to by this type.
/// </remarks>
public sealed class SyncRepository(string repositoryPath, GitCommandRunner? runner = null)
{
    private const string DefaultRemoteName = "origin";

    private readonly string _repositoryPath = Path.GetFullPath(
        string.IsNullOrWhiteSpace(repositoryPath)
            ? throw new ArgumentException("A sync repository path is required.", nameof(repositoryPath))
            : repositoryPath);

    private readonly GitCommandRunner _runner = runner ?? new GitCommandRunner();

    /// <summary>Absolute path of the sync repository on this PC.</summary>
    public string RepositoryPath => _repositoryPath;

    /// <summary>Whether the Git command line is usable on this PC.</summary>
    public GitAvailability CheckGitAvailability() => _runner.CheckAvailability();

    /// <summary>Reads the repository state, checking that Git is usable first.</summary>
    public SyncRepositoryStatus GetStatus()
    {
        _runner.EnsureAvailable();
        EnsureDirectoryExists();

        var insideWorkTree = _runner.Run(_repositoryPath, ["rev-parse", "--is-inside-work-tree"]);
        if (!insideWorkTree.Succeeded
            || !insideWorkTree.StandardOutput.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            return new SyncRepositoryStatus
            {
                IsGitRepository = false,
                HasRemote = false,
                PendingChanges = []
            };
        }

        var remotes = _runner.Run(_repositoryPath, ["remote"]);
        var hasRemote = remotes.Succeeded && remotes.StandardOutput.Trim().Length > 0;

        var branch = _runner.Run(_repositoryPath, ["symbolic-ref", "--quiet", "--short", "HEAD"]);
        var currentBranch = branch.Succeeded ? branch.StandardOutput.Trim() : null;

        return new SyncRepositoryStatus
        {
            IsGitRepository = true,
            HasRemote = hasRemote,
            PendingChanges = ReadPendingChanges(),
            CurrentBranch = currentBranch is { Length: > 0 } ? currentBranch : null
        };
    }

    /// <summary>
    /// Prepares the folder as a sync repository, creating it and its
    /// <c>.gitignore</c> when needed. Never touches an existing repository's remote.
    /// </summary>
    public void Initialize()
    {
        _runner.EnsureAvailable();
        Directory.CreateDirectory(_repositoryPath);

        var insideWorkTree = _runner.Run(_repositoryPath, ["rev-parse", "--is-inside-work-tree"]);
        if (!insideWorkTree.Succeeded
            || !insideWorkTree.StandardOutput.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            _runner.RunOrThrow(_repositoryPath, ["init"]);
        }

        WriteGitIgnore();
    }

    /// <summary>
    /// Brings in changes made on other PCs before local changes are committed.
    /// </summary>
    /// <remarks>
    /// Uses <c>--ff-only</c>: chat files are copied wholesale, so an automatic merge
    /// or rebase could silently combine two versions of the same transcript. A
    /// divergence is surfaced instead, for the user to resolve.
    /// </remarks>
    public PullOutcome Pull()
    {
        _runner.EnsureAvailable();

        var remotes = _runner.Run(_repositoryPath, ["remote"]);
        if (!remotes.Succeeded || remotes.StandardOutput.Trim().Length == 0)
        {
            return new PullOutcome(PullStatus.NoRemote, "The sync repository has no remote yet.");
        }

        if (!HasUpstream())
        {
            return new PullOutcome(
                PullStatus.NoUpstream,
                "This branch has never been pushed, so there is nothing to pull yet.");
        }

        var pull = _runner.Run(_repositoryPath, ["pull", "--ff-only"]);
        return pull.Succeeded
            ? new PullOutcome(PullStatus.UpToDate, pull.StandardOutput.Trim())
            : new PullOutcome(PullStatus.Failed, pull.ErrorMessage);
    }

    private bool HasUpstream() =>
        _runner.Run(_repositoryPath, ["rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{upstream}"]).Succeeded;

    /// <summary>
    /// Stages everything and commits. Returns <see langword="false"/> when there was
    /// nothing to commit.
    /// </summary>
    public bool Commit(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        _runner.EnsureAvailable();

        _runner.RunOrThrow(_repositoryPath, ["add", "--all"]);

        if (ReadPendingChanges().Count == 0)
        {
            return false;
        }

        _runner.RunOrThrow(_repositoryPath, ["commit", "--message", message]);
        return true;
    }

    /// <summary>Pushes the current branch, setting its upstream on the first push.</summary>
    public GitResult Push()
    {
        _runner.EnsureAvailable();

        if (HasUpstream())
        {
            return _runner.Run(_repositoryPath, ["push"]);
        }

        var branch = _runner.Run(_repositoryPath, ["symbolic-ref", "--quiet", "--short", "HEAD"]);
        var branchName = branch.StandardOutput.Trim();
        if (!branch.Succeeded || branchName.Length == 0)
        {
            return new GitResult(1, string.Empty, "The sync repository has no branch checked out, so there is nothing to push.");
        }

        return _runner.Run(_repositoryPath, ["push", "--set-upstream", DefaultRemoteName, branchName]);
    }

    /// <summary>Paths with uncommitted changes, relative to the repository root.</summary>
    private IReadOnlyList<string> ReadPendingChanges()
    {
        var status = _runner.Run(_repositoryPath, ["status", "--porcelain"]);
        if (!status.Succeeded)
        {
            return [];
        }

        return
        [
            .. status.StandardOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimEnd('\r'))
                .Where(line => line.Length > 3)
                .Select(line => line[3..].Trim())
        ];
    }

    /// <summary>
    /// Keeps local-only artifacts out of the sync repository. Backups are safety
    /// copies for this PC and would otherwise be committed and pushed.
    /// </summary>
    private void WriteGitIgnore()
    {
        var gitIgnorePath = Path.Combine(_repositoryPath, ".gitignore");
        const string backupsEntry = ".backups/";

        if (!File.Exists(gitIgnorePath))
        {
            File.WriteAllText(gitIgnorePath, $"# Local backups taken before overwriting chat files{Environment.NewLine}{backupsEntry}{Environment.NewLine}");
            return;
        }

        var lines = File.ReadAllLines(gitIgnorePath);
        if (lines.Any(line => line.Trim().TrimEnd('/') == backupsEntry.TrimEnd('/')))
        {
            return;
        }

        var addition = new List<string>(lines);
        if (addition.Count > 0 && addition[^1].Trim().Length > 0)
        {
            addition.Add(string.Empty);
        }

        addition.Add("# Local backups taken before overwriting chat files");
        addition.Add(backupsEntry);
        File.WriteAllLines(gitIgnorePath, addition);
    }

    private void EnsureDirectoryExists()
    {
        if (!Directory.Exists(_repositoryPath))
        {
            throw new GitCommandException($"The sync folder does not exist: {_repositoryPath}");
        }
    }
}
