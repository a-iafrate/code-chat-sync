using System.Diagnostics;

namespace CodeChatSync.Cli;

/// <summary>
/// Decides whether a command may run while the provider's tool is open.
/// </summary>
/// <remarks>
/// Read-only commands may bypass the check; anything that writes local chat data
/// must not, so the bypass is opt-in per command rather than global.
/// </remarks>
internal static class RunningProviderCheck
{
    internal const string AllowOptionName = "--allow-running-provider";
    private const string AllowEnvironmentVariable = "CODECHATSYNC_ALLOW_RUNNING_PROVIDER";

    /// <summary>Returns the running processes among <paramref name="processNames"/>.</summary>
    internal static IReadOnlyList<string> GetRunningProcesses(IEnumerable<string> processNames)
    {
        var running = new List<string>();
        foreach (var name in processNames)
        {
            var processes = Process.GetProcessesByName(name);
            try
            {
                if (processes.Length > 0)
                {
                    running.Add(name);
                }
            }
            finally
            {
                foreach (var process in processes)
                {
                    process.Dispose();
                }
            }
        }

        return running;
    }

    /// <summary>
    /// Whether a read-only command may skip the check. Debug builds skip it by
    /// default so the inner development loop stays usable while Visual Studio is
    /// open; Release builds require the explicit opt-in.
    /// </summary>
    internal static bool ShouldSkipForReadOnlyCommand(bool allowRunningProvider)
    {
        if (allowRunningProvider)
        {
            return true;
        }

        var configured = Environment.GetEnvironmentVariable(AllowEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var value = configured.Trim();
            return value is "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }

#if DEBUG
        return true;
#else
        return false;
#endif
    }
}
