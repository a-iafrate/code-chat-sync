using System.CommandLine;
using CodeChatSync.Providers.VisualStudio;

namespace CodeChatSync.Cli.Commands;

/// <summary>
/// Lists the Copilot chat sessions stored on this PC, without copying or changing
/// anything.
/// </summary>
internal static class DiscoverCommand
{
    internal static Command Create()
    {
        var pathArgument = new Argument<string?>("path")
        {
            Description = "Solution or folder to scope the search to. Defaults to the current directory.",
            Arity = ArgumentArity.ZeroOrOne
        };

        var allOption = new Option<bool>("--all")
        {
            Description = "List every session on this machine instead of only this project's."
        };

        var allowRunningOption = new Option<bool>(RunningProviderCheck.AllowOptionName)
        {
            Description = "Inspect sessions even while Visual Studio is running."
        };

        var command = new Command("discover", "List the Copilot chat sessions stored on this PC.");
        command.Arguments.Add(pathArgument);
        command.Options.Add(allOption);
        command.Options.Add(allowRunningOption);

        command.SetAction(parseResult => Run(
            parseResult.GetValue(pathArgument),
            parseResult.GetValue(allOption),
            parseResult.GetValue(allowRunningOption)));

        return command;
    }

    private static int Run(string? path, bool listAllSessions, bool allowRunningProvider)
    {
        if (!RunningProviderCheck.ShouldSkipForReadOnlyCommand(allowRunningProvider))
        {
            var running = RunningProviderCheck.GetRunningProcesses(["devenv"]);
            if (running.Count > 0)
            {
                Console.Error.WriteLine("Close all Visual Studio instances before discovering provider-owned files.");
                Console.Error.WriteLine(
                    $"Discovery is read-only; pass {RunningProviderCheck.AllowOptionName} to inspect anyway.");
                return 1;
            }
        }

        var fullTargetPath = Path.GetFullPath(path ?? Directory.GetCurrentDirectory());
        string projectDirectory;

        if (Directory.Exists(fullTargetPath))
        {
            projectDirectory = fullTargetPath;
        }
        else if (File.Exists(fullTargetPath))
        {
            projectDirectory = Path.GetDirectoryName(fullTargetPath)
                ?? throw new IOException("Unable to determine the solution directory.");
        }
        else
        {
            Console.Error.WriteLine($"Path not found: {fullTargetPath}");
            return 2;
        }

        var sessionStateRoot = CopilotChatDiscovery.GetDefaultSessionStateRoot();
        Console.WriteLine($"Project directory:  {projectDirectory}");
        Console.WriteLine($"Session state root: {sessionStateRoot}");

        if (!Directory.Exists(sessionStateRoot))
        {
            Console.WriteLine("No Copilot session-state directory was found for the current user.");
            return 0;
        }

        var sessions = listAllSessions
            ? CopilotChatDiscovery.DiscoverSessions(sessionStateRoot)
            : CopilotChatDiscovery.DiscoverSessionsForDirectory(projectDirectory, sessionStateRoot);

        Console.WriteLine(listAllSessions
            ? "Scope: all sessions for the current user."
            : "Scope: sessions recorded for this project directory.");
        Console.WriteLine();

        if (sessions.Count == 0)
        {
            Console.WriteLine("No matching chat sessions were found.");
            Console.WriteLine("Pass --all to list every session on this machine.");
            return 0;
        }

        long totalBytes = 0;
        foreach (var session in sessions)
        {
            var sessionBytes = session.Files.Sum(file => file.Length);
            totalBytes += sessionBytes;

            Console.WriteLine($"Session {session.Id}{(session.IsInUse ? "  [in use]" : string.Empty)}");
            Console.WriteLine($"  Title:      {Output.Summarize(session.Name)}{(session.IsUserNamed == true ? "  (user named)" : string.Empty)}");
            Console.WriteLine($"  Repository: {session.Repository ?? "(none recorded)"}{FormatBranch(session.Branch)}");
            Console.WriteLine($"  Folder:     {session.WorkingDirectory ?? "(none recorded)"}");
            Console.WriteLine($"  Client:     {session.ClientName ?? "(unknown)"}");
            Console.WriteLine($"  Created:    {Output.Format(session.CreatedAt)}");
            Console.WriteLine($"  Updated:    {Output.Format(session.UpdatedAt)}");
            Console.WriteLine($"  Files:      {session.Files.Count} ({sessionBytes:N0} bytes)");

            foreach (var file in session.Files)
            {
                Console.WriteLine($"    {file.RelativePath} ({file.Length:N0} bytes)");
            }

            Console.WriteLine();
        }

        Console.WriteLine($"Sessions found: {sessions.Count}; total size: {totalBytes:N0} bytes.");
        Console.WriteLine("Discovery reads session metadata only; it does not copy or modify chat data.");
        return 0;
    }

    private static string FormatBranch(string? branch) =>
        string.IsNullOrWhiteSpace(branch) ? string.Empty : $" ({branch})";
}
