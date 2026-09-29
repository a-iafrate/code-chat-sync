namespace CodeChatSync.Providers.Claude;

/// <summary>
/// Raised when Claude Code's storage would be read or written while Claude Code is
/// running. Its live transcripts must not be touched until it has been closed.
/// </summary>
public sealed class ClaudeCodeRunningException(IReadOnlyList<string> runningProcesses)
    : InvalidOperationException(
        $"Claude Code is running ({string.Join(", ", runningProcesses)}); close it before reading or writing its chat data.")
{
    public IReadOnlyList<string> RunningProcesses { get; } = runningProcesses;
}
