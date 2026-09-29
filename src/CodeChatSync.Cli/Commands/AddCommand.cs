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
        try
        {
            var config = LocalConfig.Load();
            var project = ProjectRegistration.Add(path ?? Directory.GetCurrentDirectory(), name, remoteOverride);
            Console.WriteLine($"Registered {project.Identity.NormalizedRemote}.");
            Console.WriteLine($"  Local path:   {project.LocalPath}");
            Console.WriteLine($"  Sync folder:  {Path.Combine(config.SyncRootPath!, "visualstudio", project.SyncFolderName)}");
            Console.WriteLine($"  Shared map:   {SharedConfig.GetPath(config.SyncRootPath!)}");
            Console.WriteLine();
            Console.WriteLine("Run 'codechatsync sync --dry-run' to preview what would be copied.");
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or DirectoryNotFoundException
            or InvalidOperationException)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }
}
