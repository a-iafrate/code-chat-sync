using System.Diagnostics;
using CodeChatSync.Providers.VisualStudio;

namespace CodeChatSync.Cli;

internal static class EntryPoint
{
    private const string AllowRunningProviderOption = "--allow-running-provider";
    private const string AllRunningProviderVariable = "CODECHATSYNC_ALLOW_RUNNING_PROVIDER";
    private const string AllSessionsOption = "--all";

    private static int Main(string[] args)
    {
        var allowRunningProvider = false;
        var listAllSessions = false;
        var positional = new List<string>(args.Length);

        foreach (var arg in args)
        {
            if (string.Equals(arg, AllowRunningProviderOption, StringComparison.OrdinalIgnoreCase))
            {
                allowRunningProvider = true;
            }
            else if (string.Equals(arg, AllSessionsOption, StringComparison.OrdinalIgnoreCase))
            {
                listAllSessions = true;
            }
            else
            {
                positional.Add(arg);
            }
        }

        if (positional.Count is 0 or > 2 || !string.Equals(positional[0], "discover", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine(
                $"Usage: codechatsync discover [<solution-or-folder-path>] [{AllSessionsOption}] [{AllowRunningProviderOption}]");
            return 2;
        }

        try
        {
            if (!ShouldSkipRunningProviderCheck(allowRunningProvider))
            {
                using var visualStudioProcesses = new ProcessCollection(Process.GetProcessesByName("devenv"));
                if (visualStudioProcesses.Count > 0)
                {
                    Console.Error.WriteLine("Close all Visual Studio instances before discovering provider-owned files.");
                    Console.Error.WriteLine($"Discovery is read-only; pass {AllowRunningProviderOption} to inspect anyway.");
                    return 1;
                }
            }

            var target = positional.Count == 2 ? positional[1] : Directory.GetCurrentDirectory();
            return Discover(target, listAllSessions);
        }
        catch (UnauthorizedAccessException exception)
        {
            Console.Error.WriteLine($"Access denied while inspecting Visual Studio data: {exception.Message}");
            return 1;
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine($"I/O error while inspecting Visual Studio data: {exception.Message}");
            return 1;
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            Console.Error.WriteLine($"Unable to verify whether Visual Studio is running: {exception.Message}");
            return 1;
        }
    }

    // Discovery only reads session metadata, so it is safe to run alongside the provider.
    // Debug builds skip the check by default to keep the inner development loop usable.
    private static bool ShouldSkipRunningProviderCheck(bool allowRunningProvider)
    {
        if (allowRunningProvider)
        {
            return true;
        }

        var configured = Environment.GetEnvironmentVariable(AllRunningProviderVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim() is "1"
                || string.Equals(configured.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        }

#if DEBUG
        return true;
#else
        return false;
#endif
    }

    private static int Discover(string targetPath, bool listAllSessions)
    {
        var fullTargetPath = Path.GetFullPath(targetPath);
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
            Console.WriteLine($"Pass {AllSessionsOption} to list every session on this machine.");
            return 0;
        }

        long totalBytes = 0;
        foreach (var session in sessions)
        {
            var sessionBytes = session.Files.Sum(file => file.Length);
            totalBytes += sessionBytes;

            Console.WriteLine($"Session {session.Id}{(session.IsInUse ? "  [in use]" : string.Empty)}");
            Console.WriteLine($"  Title:      {Summarize(session.Name)}{(session.IsUserNamed == true ? "  (user named)" : string.Empty)}");
            Console.WriteLine($"  Repository: {session.Repository ?? "(none recorded)"}{FormatBranch(session.Branch)}");
            Console.WriteLine($"  Folder:     {session.WorkingDirectory ?? "(none recorded)"}");
            Console.WriteLine($"  Client:     {session.ClientName ?? "(unknown)"}");
            Console.WriteLine($"  Created:    {Format(session.CreatedAt)}");
            Console.WriteLine($"  Updated:    {Format(session.UpdatedAt)}");
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

    /// <summary>Collapses a generated title into a single readable console line.</summary>
    private static string Summarize(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "(none recorded)";
        }

        var firstLine = title.ReplaceLineEndings(" ").Trim();
        const int maxLength = 90;
        return firstLine.Length <= maxLength ? firstLine : firstLine[..maxLength] + "...";
    }

    private static string Format(DateTimeOffset? timestamp) =>
        timestamp?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "(unknown)";

    private sealed class ProcessCollection(Process[] processes) : IDisposable
    {
        public int Count => processes.Length;

        public void Dispose()
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }
}
