using System.CommandLine;
using CodeChatSync.Core;
using CodeChatSync.Git;
using CodeChatSync.Providers.VisualStudio;

namespace CodeChatSync.Cli.Commands;

/// <summary>
/// Copies chat files between this PC and the sync folder for every registered
/// project.
/// </summary>
internal static class SyncCommand
{
    internal static Command Create()
    {
        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Report what would be copied without changing anything."
        };

        var projectOption = new Option<string?>("--project")
        {
            Description = "Sync only the project whose remote or sync folder name matches this value."
        };

        var noGitOption = new Option<bool>("--no-git")
        {
            Description = "Only copy files: do not pull, commit or push the sync repository."
        };

        var command = new Command("sync", "Sync chats between this PC and the sync folder.");
        command.Options.Add(dryRunOption);
        command.Options.Add(projectOption);
        command.Options.Add(noGitOption);

        command.SetAction(parseResult => Run(
            parseResult.GetValue(dryRunOption),
            parseResult.GetValue(projectOption),
            parseResult.GetValue(noGitOption)));

        return command;
    }

    private static int Run(bool dryRun, string? projectFilter, bool noGit)
    {
        var localConfig = LocalConfig.Load();
        if (localConfig.SyncRootPath is not { Length: > 0 } syncRoot)
        {
            Console.Error.WriteLine("No sync folder is configured on this PC.");
            Console.Error.WriteLine("Run: codechatsync config set-sync-root <path>");
            return 2;
        }

        if (!Directory.Exists(syncRoot))
        {
            Console.Error.WriteLine($"The configured sync folder no longer exists: {syncRoot}");
            return 2;
        }

        var sharedConfig = SharedConfig.Load(syncRoot);
        var workspace = new SyncWorkspace(localConfig, sharedConfig);
        var resolution = workspace.ResolveProjects();

        foreach (var unresolved in resolution.Unresolved)
        {
            Console.Error.WriteLine($"Skipping {unresolved.Remote}: {unresolved.Reason}");
        }

        var projects = resolution.Projects;
        if (projectFilter is { Length: > 0 })
        {
            projects = [.. projects.Where(project => Matches(project, projectFilter))];
            if (projects.Count == 0)
            {
                Console.Error.WriteLine($"No registered project matches '{projectFilter}'.");
                return 2;
            }
        }

        if (projects.Count == 0)
        {
            Console.WriteLine("No projects are registered on this PC.");
            Console.WriteLine("Run 'codechatsync add' from inside a project's Git repository.");
            return 0;
        }

        Console.WriteLine($"Sync folder: {syncRoot}");
        if (dryRun)
        {
            Console.WriteLine("Dry run: nothing will be copied.");
        }

        Console.WriteLine();

        var repository = noGit || dryRun ? null : OpenRepository(syncRoot);
        if (repository is not null && !PullBeforeSync(repository))
        {
            return 4;
        }

        var provider = new VisualStudioChatProvider();
        var service = new ChatSyncService(new ProcessGuard());
        var hasConflicts = false;
        var hasBlockedPulls = false;
        var pushedCount = 0;

        foreach (var project in projects)
        {
            Console.WriteLine($"{project.Identity.NormalizedRemote} -> {provider.Id}/{project.SyncFolderName}");

            var statePath = workspace.GetStatePath(provider.Id, project);
            var state = SyncState.Load(statePath);

            var report = service.Sync(provider, project, syncRoot, state, dryRun);

            if (!dryRun)
            {
                state.Save(statePath);
            }

            Report(report);
            hasConflicts |= report.HasConflicts;
            hasBlockedPulls |= report.HasBlockedPulls;
            pushedCount += report.PushedCount;
            Console.WriteLine();
        }

        if (repository is not null)
        {
            PublishAfterSync(repository, pushedCount);
        }

        if (hasBlockedPulls)
        {
            Console.WriteLine("Some files still need Visual Studio to be closed; pushes completed as usual.");
            Console.WriteLine("Run 'codechatsync sync' again after closing it to finish pulling them.");
        }

        if (hasConflicts)
        {
            Console.WriteLine("Conflicts were left untouched on both sides; resolve them before syncing again.");
        }

        return hasConflicts ? 3 : 0;
    }

    /// <summary>
    /// Returns the sync repository when Git can be used, or <see langword="null"/>
    /// after explaining why only file copying will happen.
    /// </summary>
    private static SyncRepository? OpenRepository(string syncRoot)
    {
        var repository = new SyncRepository(syncRoot);

        var availability = repository.CheckGitAvailability();
        if (!availability.IsAvailable)
        {
            Console.WriteLine($"Git is not available ({availability.Description}); syncing files only.");
            Console.WriteLine();
            return null;
        }

        SyncRepositoryStatus status;
        try
        {
            status = repository.GetStatus();
        }
        catch (GitCommandException exception)
        {
            Console.WriteLine($"Git is unusable here ({exception.Message}); syncing files only.");
            Console.WriteLine();
            return null;
        }

        if (!status.IsGitRepository)
        {
            Console.WriteLine("The sync folder is not a Git repository; syncing files only.");
            Console.WriteLine("Run 'codechatsync config set-sync-root <path> --init' to turn it into one.");
            Console.WriteLine();
            return null;
        }

        return repository;
    }

    /// <summary>
    /// Brings in the other PCs' chats before local files are compared, so the sync
    /// sees the newest shared state.
    /// </summary>
    private static bool PullBeforeSync(SyncRepository repository)
    {
        var pull = repository.Pull();

        switch (pull.Status)
        {
            case PullStatus.UpToDate:
                Console.WriteLine("Pulled the latest chats from the sync remote.");
                break;

            case PullStatus.NoRemote:
                Console.WriteLine("The sync repository has no remote yet; working locally.");
                break;

            case PullStatus.NoUpstream:
                Console.WriteLine("First push from this PC: nothing to pull yet.");
                break;

            case PullStatus.Failed:
                Console.Error.WriteLine("Could not pull the sync repository, so nothing was changed locally:");
                Console.Error.WriteLine(Indent(pull.Message));
                Console.Error.WriteLine("Resolve it in the sync folder, or run 'codechatsync sync --no-git' to copy files only.");
                return false;
        }

        Console.WriteLine();
        return true;
    }

    /// <summary>Commits and pushes what this PC just copied into the sync folder.</summary>
    private static void PublishAfterSync(SyncRepository repository, int pushedCount)
    {
        try
        {
            var message = $"Sync chats from {Environment.MachineName} ({pushedCount} file(s))";
            if (!repository.Commit(message))
            {
                Console.WriteLine("Nothing new to commit in the sync repository.");
                return;
            }

            Console.WriteLine("Committed the updated chats.");

            if (!repository.GetStatus().HasRemote)
            {
                Console.WriteLine("No remote is configured, so the commit stays on this PC.");
                return;
            }

            var push = repository.Push();
            Console.WriteLine(push.Succeeded
                ? "Pushed the sync repository."
                : $"Could not push the sync repository:{Environment.NewLine}{Indent(push.ErrorMessage)}");
        }
        catch (GitCommandException exception)
        {
            Console.Error.WriteLine($"Git failed after the files were copied: {exception.Message}");
        }
    }

    private static string Indent(string text) =>
        string.Join(
            Environment.NewLine,
            text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => "  " + line.TrimEnd('\r')));

    private static void Report(SyncReport report)
    {
        foreach (var entry in report.Entries.Where(entry => entry.Action is not SyncAction.Unchanged))
        {
            Console.WriteLine($"  {entry.Action,-9} {entry.RelativePath}{FormatReason(entry.Reason)}");

            if (entry.BackupPath is { Length: > 0 } backupPath)
            {
                Console.WriteLine($"            backup: {backupPath}");
            }
        }

        Console.WriteLine(
            $"  {report.PushedCount} pushed, {report.PulledCount} pulled, {report.UnchangedCount} unchanged, " +
            $"{report.SkippedCount} skipped, {report.ConflictCount} conflicts.");
    }

    private static string FormatReason(string? reason) =>
        string.IsNullOrWhiteSpace(reason) ? string.Empty : $"  ({reason})";

    private static bool Matches(ProjectInfo project, string filter) =>
        project.Identity.NormalizedRemote.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || project.SyncFolderName.Equals(filter, StringComparison.OrdinalIgnoreCase)
        || project.Identity.Slug.Equals(filter, StringComparison.OrdinalIgnoreCase);
}
