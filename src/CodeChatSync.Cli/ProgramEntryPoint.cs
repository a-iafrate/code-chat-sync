using System.CommandLine;
using CodeChatSync.Cli.Commands;

namespace CodeChatSync.Cli;

internal static class ProgramEntryPoint
{
    private static int Main(string[] args)
    {
        var rootCommand = new RootCommand("Sync AI chats across your PCs through your own private Git repository.");
        rootCommand.Subcommands.Add(DiscoverCommand.Create());
        rootCommand.Subcommands.Add(ConfigCommand.Create());
        rootCommand.Subcommands.Add(AddCommand.Create());
        rootCommand.Subcommands.Add(ListCommand.Create());
        rootCommand.Subcommands.Add(SyncCommand.Create());

        try
        {
            return rootCommand.Parse(args).Invoke();
        }
        catch (InvalidDataException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
        catch (UnauthorizedAccessException exception)
        {
            Console.Error.WriteLine($"Access denied: {exception.Message}");
            return 1;
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine($"I/O error: {exception.Message}");
            return 1;
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            Console.Error.WriteLine($"Unable to inspect running processes: {exception.Message}");
            return 1;
        }
    }
}
