using CodeChatSync.Core;

namespace CodeChatSync.Tests.TestSupport;

/// <summary>Process guard whose running set can change between checks.</summary>
internal sealed class MutableProcessGuard : IProcessGuard
{
    public HashSet<string> RunningProcesses { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many times the guard was queried.</summary>
    public int QueryCount { get; private set; }

    public IReadOnlyList<string> GetRunningProcesses(IEnumerable<string> processNames)
    {
        QueryCount++;
        return [.. processNames.Where(RunningProcesses.Contains)];
    }
}

/// <summary>Clock the test moves by hand, so timing rules need no real waiting.</summary>
internal sealed class TestTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan amount) => _now = _now.Add(amount);
}
