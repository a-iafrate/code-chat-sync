using System.CommandLine;
using CodeChatSync.Core;
using CodeChatSync.Git;

namespace CodeChatSync.Cli.Commands;

/// <summary>Inspects and updates the per-PC configuration.</summary>
internal static class ConfigCommand
{
    internal static Command Create()
    {
        var command = new Command("config", "Inspect or change this PC's configuration.");
        command.Subcommands.Add(CreateShow());
        command.Subcommands.Add(CreateSetSyncRoot());
        return command;
    }

    private static Command CreateShow()
    {
        var command = new Command("show", "Show this PC's configuration.");
        command.SetAction(_ =>
        {
            var config = LocalConfig.Load();

            Console.WriteLine($"Config file: {LocalConfig.GetDefaultPath()}");
            Console.WriteLine($"Sync folder: {config.SyncRootPath ?? "(not configured)"}");
            Console.WriteLine($"Baselines:   {LocalConfig.GetDefaultStateDirectory()}");
            Console.WriteLine($"Projects:    {config.Projects.Count}");

            if (Environment.GetEnvironmentVariable(LocalConfig.HomeEnvironmentVariable) is { Length: > 0 } home)
            {
                Console.WriteLine($"Home override: {LocalConfig.HomeEnvironmentVariable}={home}");
            }

            if (config.SyncRootPath is { Length: > 0 } syncRoot)
            {
                Console.WriteLine($"Shared map:  {SharedConfig.GetPath(syncRoot)}");
            }

            Console.WriteLine();
            ReportGit(config.SyncRootPath);

            return 0;
        });

        return command;
    }

    private static void ReportGit(string? syncRootPath)
    {
        var availability = new GitCommandRunner().CheckAvailability();
        if (!availability.IsAvailable)
        {
            Console.WriteLine($"Git:         not available - {availability.Description}");
            Console.WriteLine("             Chats still sync to the sync folder; only commit/push/pull are unavailable.");
            return;
        }

        Console.WriteLine($"Git:         {availability.Description}");

        if (syncRootPath is not { Length: > 0 } || !Directory.Exists(syncRootPath))
        {
            return;
        }

        try
        {
            var status = new SyncRepository(syncRootPath).GetStatus();
            Console.WriteLine($"Sync repo:   {(status.IsGitRepository ? "initialized" : "not a Git repository")}");

            if (status.IsGitRepository)
            {
                Console.WriteLine($"  Branch:    {status.CurrentBranch ?? "(detached HEAD)"}");
                Console.WriteLine($"  Remote:    {(status.HasRemote ? "configured" : "none configured")}");
                Console.WriteLine($"  Pending:   {status.PendingChanges.Count} uncommitted change(s)");
            }
        }
        catch (GitCommandException exception)
        {
            Console.WriteLine($"Sync repo:   unavailable - {exception.Message}");
        }
    }

    private static Command CreateSetSyncRoot()
    {
        var pathArgument = new Argument<string>("path")
        {
            Description = "Folder of the private sync repository."
        };

        var initOption = new Option<bool>("--init")
        {
            Description = "Initialize the folder as a Git repository if it is not one already."
        };

        var command = new Command("set-sync-root", "Set the folder of the private sync repository.");
        command.Arguments.Add(pathArgument);
        command.Options.Add(initOption);

        command.SetAction(parseResult =>
        {
            var path = Path.GetFullPath(parseResult.GetValue(pathArgument)!);
            var initialize = parseResult.GetValue(initOption);

            if (!Directory.Exists(path) && !initialize)
            {
                Console.Error.WriteLine($"Folder not found: {path}");
                Console.Error.WriteLine("Clone your private sync repo there, or pass --init to create it.");
                return 2;
            }

            if (initialize)
            {
                try
                {
                    new SyncRepository(path).Initialize();
                    Console.WriteLine($"Initialized a Git repository at {path}.");
                }
                catch (GitCommandException exception)
                {
                    Console.Error.WriteLine(exception.Message);
                    return 1;
                }
            }

            var config = LocalConfig.Load();
            config.SyncRootPath = path;
            config.Save();

            Console.WriteLine($"Sync folder set to {path}.");
            Console.WriteLine($"Saved to {LocalConfig.GetDefaultPath()}.");
            return 0;
        });

        return command;
    }
}
