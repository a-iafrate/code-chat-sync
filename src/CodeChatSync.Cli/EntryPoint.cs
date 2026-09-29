using System.Diagnostics;

namespace CodeChatSync.Cli;

internal static class EntryPoint
{
    private const string AllowRunningProviderOption = "--allow-running-provider";
    private const string AllowRunningProviderVariable = "CODECHATSYNC_ALLOW_RUNNING_PROVIDER";

    private static int Main(string[] args)
    {
        var allowRunningProvider = false;
        var remainingArgs = new List<string>(args.Length);
        foreach (var arg in args)
        {
            if (string.Equals(arg, AllowRunningProviderOption, StringComparison.OrdinalIgnoreCase))
            {
                allowRunningProvider = true;
                continue;
            }

            remainingArgs.Add(arg);
        }

        if (remainingArgs.Count != 2 || !string.Equals(remainingArgs[0], "discover", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"Usage: codechatsync discover <solution-path> [{AllowRunningProviderOption}]");
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

            return Discover(remainingArgs[1]);
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

    // Discovery only lists paths and sizes, so it is safe to run alongside the provider.
    // Debug builds skip the check by default to keep the inner development loop usable.
    private static bool ShouldSkipRunningProviderCheck(bool allowRunningProvider)
    {
        if (allowRunningProvider)
        {
            return true;
        }

        var configured = Environment.GetEnvironmentVariable(AllowRunningProviderVariable);
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

    private static int Discover(string solutionPath)
    {
        var fullSolutionPath = Path.GetFullPath(solutionPath);
        if (!File.Exists(fullSolutionPath))
        {
            Console.Error.WriteLine($"Solution file not found: {fullSolutionPath}");
            return 2;
        }

        var extension = Path.GetExtension(fullSolutionPath);
        if (!string.Equals(extension, ".sln", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(extension, ".slnx", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("The path must point to a .sln or .slnx solution file.");
            return 2;
        }

        var solutionDirectory = Path.GetDirectoryName(fullSolutionPath)
            ?? throw new IOException("Unable to determine the solution directory.");
        var visualStudioDirectory = Path.Combine(solutionDirectory, ".vs");

        Console.WriteLine($"Solution: {fullSolutionPath}");
        Console.WriteLine($"Visual Studio data directory: {visualStudioDirectory}");

        if (!Directory.Exists(visualStudioDirectory))
        {
            Console.WriteLine("No .vs directory was found beside the solution.");
            return 0;
        }

        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false
        };

        var files = Directory.EnumerateFiles(visualStudioDirectory, "*", enumerationOptions)
            .Select(path => new FileInfo(path))
            .OrderBy(file => Path.GetRelativePath(visualStudioDirectory, file.FullName), StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var candidateCount = 0;
        long totalBytes = 0;
        foreach (var file in files)
        {
            var relativePath = Path.GetRelativePath(visualStudioDirectory, file.FullName);
            var looksChatRelated = LooksChatRelated(relativePath);
            if (looksChatRelated)
            {
                candidateCount++;
            }

            totalBytes += file.Length;
            var label = looksChatRelated ? "candidate" : "file";
            Console.WriteLine($"{label,-9} {relativePath} ({file.Length:N0} bytes)");
        }

        Console.WriteLine($"Files inspected: {files.Length}; name-based candidates: {candidateCount}; total size: {totalBytes:N0} bytes.");
        Console.WriteLine("Discovery lists paths and sizes only; it does not read or modify file contents.");
        return 0;
    }

    // The first segment under .vs is the solution's own folder name, which would
    // otherwise match projects whose name happens to contain "chat" or "copilot".
    private static bool LooksChatRelated(string relativePath)
    {
        var segments = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        return segments
            .Skip(1)
            .Any(segment => segment.Contains("chat", StringComparison.OrdinalIgnoreCase)
                || segment.Contains("copilot", StringComparison.OrdinalIgnoreCase));
    }

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
