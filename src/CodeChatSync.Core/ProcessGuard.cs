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
}
