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

    /// <summary>Reads the origin URL, or null when no origin is configured.</summary>
    public string? GetOriginRemoteUrl()
    {
        EnsureRepositoryRoot();
        if (!HasOrigin())
        {
            return null;
        }

        return _runner.RunOrThrow(_repositoryPath, ["remote", "get-url", "origin"]).StandardOutput.Trim();
    }

    /// <summary>Sets the origin of this sync repository without changing other remotes.</summary>
    public void SetOriginRemoteUrl(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        var trimmed = url.Trim();
        GitRemoteUrlGuard.ThrowIfContainsPassword(trimmed, nameof(url));

        EnsureRepositoryRoot();
        var arguments = HasOrigin()
            ? new[] { "remote", "set-url", "origin", trimmed }
            : new[] { "remote", "add", "origin", trimmed };
        var result = _runner.Run(_repositoryPath, arguments);
        if (!result.Succeeded)
        {
            throw new GitCommandException(
                $"Could not configure origin (git exit {result.ExitCode}). Check the URL and repository permissions.");
        }
    }

    private bool HasOrigin()
    {
        var result = _runner.RunOrThrow(_repositoryPath, ["remote"]);
        return result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Any(name => string.Equals(name.Trim(), "origin", StringComparison.Ordinal));
    }

    private void EnsureRepositoryRoot()
    {
        _runner.EnsureAvailable();
        EnsureDirectoryExists();
        var result = _runner.Run(_repositoryPath, ["rev-parse", "--show-toplevel"]);
        if (!result.Succeeded || !Path.GetFullPath(result.StandardOutput.Trim()).Equals(
                _repositoryPath.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            throw new GitCommandException("The sync folder must be the root of its own Git repository, not inside another project.");
        }
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
        EnsureVerbatimContent();
    }

    /// <summary>
    /// Makes Git store and check out chat files exactly as they are, with no line-ending
    /// translation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Chats are copied wholesale and compared by content hash, so a byte Git changes on
    /// its own is indistinguishable from an edit made on another PC. With the common
    /// <c>core.autocrlf=true</c> setting, checking out a chat file rewrites its line
    /// endings; the sync then sees the sync folder and this PC's copy as both changed
    /// since the last run and reports a conflict on a file nobody touched.
    /// </para>
    /// <para>
    /// Applied to existing repositories too, not only at <see cref="Initialize"/> time,
    /// since the setting is usually inherited from the user's global configuration long
    /// after the sync folder was created. The next commit then records the files as they
    /// really are on disk, once, and they stay stable afterwards.
    /// </para>
    /// </remarks>
    public void EnsureVerbatimContent()
    {
        WriteGitAttributes();

        // Belt and braces: .gitattributes already wins for tracked paths, but an explicit
        // repository-level setting also covers the attributes file itself and makes the
        // intent visible to anyone inspecting the sync folder.
        _runner.Run(_repositoryPath, ["config", "core.autocrlf", "false"]);
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
            return PullWithoutUpstream();
        }

        var pull = _runner.Run(_repositoryPath, ["pull", "--ff-only"]);
        return pull.Succeeded
            ? new PullOutcome(PullStatus.UpToDate, pull.StandardOutput.Trim())
            : new PullOutcome(PullStatus.Failed, pull.ErrorMessage);
    }

    /// <summary>
    /// Handles a branch without upstream: an empty remote means this is the first push,
    /// while a remote that already has this branch (another PC pushed first) must be
    /// pulled and tracked before any local change is made.
    /// </summary>
    private PullOutcome PullWithoutUpstream()
    {
        var fetch = _runner.Run(_repositoryPath, ["fetch", DefaultRemoteName]);
        if (!fetch.Succeeded)
        {
            return new PullOutcome(
                PullStatus.Failed,
                $"Could not reach the sync remote to check for chats from other PCs:{Environment.NewLine}{fetch.ErrorMessage}");
        }

        var branch = _runner.Run(_repositoryPath, ["symbolic-ref", "--quiet", "--short", "HEAD"]);
        var branchName = branch.StandardOutput.Trim();
        if (!branch.Succeeded || branchName.Length == 0)
        {
            return new PullOutcome(PullStatus.Failed, "The sync repository has no branch checked out.");
        }

        var remoteBranches = _runner.Run(
            _repositoryPath,
            ["for-each-ref", "--format=%(refname:strip=3)", $"refs/remotes/{DefaultRemoteName}/"]);
        var names = remoteBranches.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(name => name.Trim())
            .Where(name => name.Length > 0 && name != "HEAD")
            .ToArray();

        if (names.Length == 0)
        {
            return new PullOutcome(
                PullStatus.NoUpstream,
                "This branch has never been pushed, so there is nothing to pull yet.");
        }

        if (!names.Contains(branchName, StringComparer.Ordinal))
        {
            return new PullOutcome(
                PullStatus.Failed,
                $"The sync remote has chats on '{string.Join("', '", names)}' but not on the local branch '{branchName}'. "
                + "Check out the remote branch in the sync folder so this PC does not publish a separate history.");
        }

        var pull = PullIntoBranch(branchName);
        if (!pull.Succeeded)
        {
            return new PullOutcome(PullStatus.Failed, pull.ErrorMessage);
        }

        var track = _runner.Run(_repositoryPath, ["branch", $"--set-upstream-to={DefaultRemoteName}/{branchName}"]);
        return track.Succeeded
            ? new PullOutcome(PullStatus.UpToDate, pull.StandardOutput.Trim())
            : new PullOutcome(PullStatus.Failed, track.ErrorMessage);
    }

    private bool HasUpstream() =>
        _runner.Run(_repositoryPath, ["rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{upstream}"]).Succeeded;

    /// <summary>
    /// Fast-forwards <paramref name="branchName"/> from the remote. On a branch with no
    /// commit yet, the untracked <c>.gitignore</c> and <c>.gitattributes</c> written by
    /// <see cref="Initialize"/> would block the checkout, so they are set aside and their
    /// entries merged back afterwards.
    /// </summary>
    private GitResult PullIntoBranch(string branchName)
    {
        string[] arguments = ["pull", "--ff-only", DefaultRemoteName, branchName];
        var hasCommit = _runner.Run(_repositoryPath, ["rev-parse", "--verify", "--quiet", "HEAD"]).Succeeded;

        var setAside = hasCommit
            ? []
            : new[] { ".gitignore", ".gitattributes" }
                .Select(name => Path.Combine(_repositoryPath, name))
                .Where(File.Exists)
                .ToDictionary(path => path, File.ReadAllBytes);

        if (setAside.Count == 0)
        {
            return _runner.Run(_repositoryPath, arguments);
        }

        foreach (var path in setAside.Keys)
        {
            File.Delete(path);
        }

        var pull = _runner.Run(_repositoryPath, arguments);
        if (!pull.Succeeded)
        {
            foreach (var (path, original) in setAside)
            {
                if (!File.Exists(path))
                {
                    File.WriteAllBytes(path, original);
                }
            }

            return pull;
        }

        WriteGitIgnore();
        WriteGitAttributes();
        return pull;
    }

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

    /// <summary>
    /// Marks every path as binary for Git's purposes, so chat files survive a commit and
    /// checkout byte for byte. See <see cref="EnsureVerbatimContent"/> for why.
    /// </summary>
    private void WriteGitAttributes()
    {
        var gitAttributesPath = Path.Combine(_repositoryPath, ".gitattributes");
        const string verbatimEntry = "* -text";

        if (!File.Exists(gitAttributesPath))
        {
            File.WriteAllText(
                gitAttributesPath,
                $"# Chats are compared byte for byte: Git must not translate line endings{Environment.NewLine}{verbatimEntry}{Environment.NewLine}");
            return;
        }

        var lines = File.ReadAllLines(gitAttributesPath);
        if (lines.Any(line => line.Trim() == verbatimEntry))
        {
            return;
        }

        var addition = new List<string>(lines);
        if (addition.Count > 0 && addition[^1].Trim().Length > 0)
        {
            addition.Add(string.Empty);
        }

        addition.Add("# Chats are compared byte for byte: Git must not translate line endings");
        addition.Add(verbatimEntry);
        File.WriteAllLines(gitAttributesPath, addition);
    }

    private void EnsureDirectoryExists()
    {
        if (!Directory.Exists(_repositoryPath))
        {
            throw new GitCommandException($"The sync folder does not exist: {_repositoryPath}");
        }
    }
}
