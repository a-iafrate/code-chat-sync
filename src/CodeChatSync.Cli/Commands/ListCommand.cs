using System.CommandLine;
using CodeChatSync.Core;

namespace CodeChatSync.Cli.Commands;

/// <summary>Lists the projects registered on this PC.</summary>
internal static class ListCommand
{
    internal static Command Create()
    {
        var command = new Command("list", "List the projects registered on this PC.");

        command.SetAction(_ =>
        {
            var config = LocalConfig.Load();
            if (config.Projects.Count == 0)
            {
                Console.WriteLine("No projects are registered on this PC.");
                Console.WriteLine("Run 'codechatsync add' from inside a project's Git repository.");
                return 0;
            }

            var sharedConfig = config.SyncRootPath is { Length: > 0 } syncRoot && Directory.Exists(syncRoot)
                ? SharedConfig.Load(syncRoot)
                : new SharedConfig();

            foreach (var entry in config.Projects)
            {
                ProjectIdentity.TryFromRemote(entry.Remote, out var identity);
                var name = identity is null ? null : sharedConfig.Find(identity)?.Name;

                Console.WriteLine(entry.Remote);
                Console.WriteLine($"  Sync folder name: {name ?? identity?.Slug ?? "(unknown)"}");
                Console.WriteLine($"  Local path:       {entry.LocalPath}{(Directory.Exists(entry.LocalPath) ? string.Empty : "  [missing]")}");
                Console.WriteLine();
            }

            Console.WriteLine($"Projects registered: {config.Projects.Count}.");
            return 0;
        });

        return command;
    }
}
