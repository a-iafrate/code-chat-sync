using System.CommandLine;
using CodeChatSync.Core;
using CodeChatSync.Git;

namespace CodeChatSync.Cli.Commands;

/// <summary>
/// Registers a project for syncing, identifying it by its Git remote so the same
/// project is recognized on every PC regardless of where it was cloned.
/// </summary>
internal static class AddCommand
{
    internal static Command Create()
    {
        var pathArgument = new Argument<string?>("path")
        {
            Description = "Folder inside the project's Git repository. Defaults to the current directory.",
            Arity = ArgumentArity.ZeroOrOne
        };

        var nameOption = new Option<string?>("--name")
        {
            Description = "Folder name for the project in the sync repository. Defaults to a name derived from the remote."
        };

        var remoteOption = new Option<string?>("--remote")
        {
            Description = "Use this Git remote instead of the one configured in the repository."
        };

        var command = new Command("add", "Register a project for chat syncing.");
        command.Arguments.Add(pathArgument);
        command.Options.Add(nameOption);
        command.Options.Add(remoteOption);

        command.SetAction(parseResult => Run(
            parseResult.GetValue(pathArgument),
            parseResult.GetValue(nameOption),
            parseResult.GetValue(remoteOption)));

        return command;
    }

    private static int Run(string? path, string? name, string? remoteOverride)
    {
        var projectPath = Path.GetFullPath(path ?? Directory.GetCurrentDirectory());
        if (!Directory.Exists(projectPath))
        {
            Console.Error.WriteLine($"Folder not found: {projectPath}");
            return 2;
        }

        var config = LocalConfig.Load();
        if (config.SyncRootPath is not { Length: > 0 } syncRoot)
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

        var repositoryRoot = GitRemoteReader.FindRepositoryRoot(projectPath);
        if (repositoryRoot is null && remoteOverride is null)
        {
            Console.Error.WriteLine($"'{projectPath}' is not inside a Git repository.");
            Console.Error.WriteLine("A project is identified by its Git remote; pass --remote to register it anyway.");
            return 2;
        }

        var remote = remoteOverride ?? GitRemoteReader.FindPrimaryRemoteUrl(repositoryRoot!);
        if (string.IsNullOrWhiteSpace(remote))
        {
            Console.Error.WriteLine($"No usable Git remote was found for '{repositoryRoot ?? projectPath}'.");
            Console.Error.WriteLine("Add an 'origin' remote, or pass --remote to choose one explicitly.");
            return 2;
        }

        if (!ProjectIdentity.TryFromRemote(remote, out var identity) || identity is null)
        {
            Console.Error.WriteLine($"Unable to derive a project identity from remote '{remote}'.");
            return 2;
        }

        // Register the repository root, so chat sessions recorded in subfolders still match.
        var registeredPath = repositoryRoot ?? projectPath;

        SharedConfig sharedConfig;
        SharedProjectEntry entry;
        try
        {
            sharedConfig = SharedConfig.Load(syncRoot);
            entry = sharedConfig.AddOrUpdate(identity, name);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
        catch (InvalidDataException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }

        var isNew = config.AddOrUpdate(identity, registeredPath);
        config.Save();
        sharedConfig.Save(syncRoot);

        Console.WriteLine(isNew
            ? $"Registered {identity.NormalizedRemote}."
            : $"Updated {identity.NormalizedRemote}.");
        Console.WriteLine($"  Local path:   {registeredPath}");
        Console.WriteLine($"  Sync folder:  {Path.Combine(syncRoot, "visualstudio", entry.Name)}");
        Console.WriteLine($"  Shared map:   {SharedConfig.GetPath(syncRoot)}");
        Console.WriteLine();
        Console.WriteLine("Run 'codechatsync sync --dry-run' to preview what would be copied.");
        return 0;
    }
}
