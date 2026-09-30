using System.Diagnostics;

namespace CodeChatSync.Core;

/// <summary>
/// Reports whether a provider's tool is currently running. Writes to local chat
/// data are only allowed while it is closed.
/// </summary>
public interface IProcessGuard
{
    /// <summary>Returns the running process names matching <paramref name="processNames"/>.</summary>
    IReadOnlyList<string> GetRunningProcesses(IEnumerable<string> processNames);
}

/// <summary>Queries the operating system for running processes.</summary>
public sealed class ProcessGuard : IProcessGuard
{
    public IReadOnlyList<string> GetRunningProcesses(IEnumerable<string> processNames)
    {
        ArgumentNullException.ThrowIfNull(processNames);

        var running = new List<string>();
        foreach (var name in processNames)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(name);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Treat an unreadable process list as "running" so callers stay on the safe side.
                running.Add(name);
                continue;
            }

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
    /// Returns the guard used for sync runs: the inner guard, except that the
    /// process names of providers whose check the user skipped are never reported.
    /// Watchers keep using the real guard to detect closes; the host decides which
    /// running providers hold back automatic sync.
    /// </summary>
    public static IProcessGuard ForSync(IProcessGuard inner, LocalConfig config, IEnumerable<IChatProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(providers);

        var ignored = providers
            .Where(provider => config.IsRunningCheckSkipped(provider.Id))
            .SelectMany(provider => provider.ProcessNames)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ignored.Count == 0 ? inner : new FilteringProcessGuard(inner, ignored);
    }

    private sealed class FilteringProcessGuard(IProcessGuard inner, HashSet<string> ignored) : IProcessGuard
    {
        public IReadOnlyList<string> GetRunningProcesses(IEnumerable<string> processNames) =>
            inner.GetRunningProcesses(processNames.Where(name => !ignored.Contains(name)).ToArray());
    }
}
