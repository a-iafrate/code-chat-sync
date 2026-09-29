using CodeChatSync.Core;

namespace CodeChatSync.Tests.TestSupport;

/// <summary>Reports a fixed set of running processes, without querying the OS.</summary>
internal sealed class FakeProcessGuard(params string[] runningProcesses) : IProcessGuard
{
    private readonly string[] _runningProcesses = runningProcesses;

    public IReadOnlyList<string> GetRunningProcesses(IEnumerable<string> processNames) =>
        [.. processNames.Where(name => _runningProcesses.Contains(name, StringComparer.OrdinalIgnoreCase))];
}
