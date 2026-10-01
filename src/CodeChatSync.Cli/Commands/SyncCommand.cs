using System.CommandLine;
using CodeChatSync.Core;
using CodeChatSync.Git;
using CodeChatSync.Providers.Claude;
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

        Console.WriteLine($"Sync folder: {syncRoot}");
        if (dryRun)
        {
            Console.WriteLine("Dry run: nothing will be copied.");
        }

        var publisher = dryRun || noGit ? null : CreatePublisher(syncRoot);
        Console.WriteLine();

        var processGuard = new ProcessGuard();
        var visualStudioProvider = new VisualStudioChatProvider();
        var syncGuard = ProcessGuard.ForSync(
            processGuard, localConfig, [visualStudioProvider, new ClaudeCodeChatProvider(processGuard)]);
        var providers = new IChatProvider[]
        {
            visualStudioProvider,
            new ClaudeCodeChatProvider(syncGuard)
        };
        var orchestrator = new SyncOrchestrator(
            providers,
            new SyncWorkspace(localConfig, SharedConfig.Load(syncRoot)),
            new ChatSyncService(syncGuard),
            publisher);

        SyncRunResult result;
        try
        {
            result = orchestrator.Run(new SyncRunOptions { DryRun = dryRun, ProjectFilter = projectFilter });
        }
        catch (ClaudeCodeRunningException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }

        foreach (var unresolved in result.Unresolved)
        {
            Console.Error.WriteLine($"Skipping {unresolved.Remote}: {unresolved.Reason}");
        }

        if (result.Aborted)
        {
            Console.Error.WriteLine(result.AbortReason);
            Console.Error.WriteLine("Resolve it in the sync folder, or run 'codechatsync sync --no-git' to copy files only.");
            return 4;
        }

        if (result.PrepareMessage is { Length: > 0 } prepareMessage)
        {
            Console.WriteLine(prepareMessage);
            Console.WriteLine();
        }

        if (result.Projects.Count == 0)
        {
            if (projectFilter is { Length: > 0 })
            {
                Console.Error.WriteLine($"No registered project matches '{projectFilter}'.");
                return 2;
            }

            Console.WriteLine("No projects are registered on this PC.");
            Console.WriteLine("Run 'codechatsync add' from inside a project's Git repository.");
            return 0;
        }

        foreach (var project in result.Projects)
        {
            Console.WriteLine($"{project.Project.Identity.NormalizedRemote} -> {project.ProviderId}/{project.Project.SyncFolderName}");
            Report(project.Report);
            Console.WriteLine();
        }

        if (result.PublishMessage is { Length: > 0 } publishMessage)
        {
            Console.WriteLine(publishMessage);
        }

        if (result.HasBlockedPulls)
        {
            Console.WriteLine("Some files still need their provider to be closed; pushes completed as usual.");
            Console.WriteLine("Run 'codechatsync sync' again after closing the provider to finish pulling them.");
        }

        if (result.HasConflicts)
        {
            Console.WriteLine("Conflicts were left untouched on both sides; resolve them before syncing again.");
        }

        return result.HasConflicts ? 3 : 0;
    }

    /// <summary>
    /// Builds the Git publisher, or explains why this run will only copy files.
    /// </summary>
    private static ISyncPublisher? CreatePublisher(string syncRoot)
    {
        var publisher = GitSyncPublisher.TryCreate(syncRoot, out var unavailableReason);
        if (unavailableReason is { Length: > 0 })
        {
            Console.WriteLine(unavailableReason);
        }

        return publisher;
    }

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

        if (report.Registration is { } registration)
        {
            if (registration.ListedCount > 0)
            {
                Console.WriteLine($"  Added {registration.ListedCount} restored chat(s) to the tool's chat list.");
            }

            if (registration.RegisteredCount > 0)
            {
                Console.WriteLine($"  Announced {registration.RegisteredCount} restored chat(s) to the tool's index.");
            }

            if (registration.Reason is { Length: > 0 } reason)
            {
                Console.WriteLine($"  {reason}");
            }
        }

        Console.WriteLine(
            $"  {report.PushedCount} pushed, {report.PulledCount} pulled, {report.UnchangedCount} unchanged, " +
            $"{report.SkippedCount} skipped, {report.ConflictCount} conflicts.");
    }

    private static string FormatReason(string? reason) =>
        string.IsNullOrWhiteSpace(reason) ? string.Empty : $"  ({reason})";
}
