using CodeChatSync.Core;

namespace CodeChatSync.Tests;

/// <summary>
/// Covers the contract that matters for the rest of the suite: with
/// <c>CODECHATSYNC_TRACE_LOG</c> unset — true for every other test, and for ordinary use
/// of the CLI and the app — tracing is a complete no-op that never touches the disk and
/// never changes what a traced step returns or throws.
/// </summary>
/// <remarks>
/// The enabled path (actually writing the file) is not covered here: the destination is
/// cached once per process on first use, by design, so later tests in the same run could
/// not reliably toggle it back off. It is exercised directly against a real sync instead.
/// </remarks>
public sealed class SyncTraceTests
{
    [Fact]
    public void IsEnabled_IsFalseWithoutTheEnvironmentVariable()
    {
        Assert.False(SyncTrace.IsEnabled);
    }

    [Fact]
    public void Log_DoesNothingObservableWhenDisabled()
    {
        var exception = Record.Exception(() => SyncTrace.Log("should be silently ignored"));

        Assert.Null(exception);
    }

    [Fact]
    public void Time_ReturnsTheStepsResultWhenDisabled()
    {
        var result = SyncTrace.Time("label", () => 42);

        Assert.Equal(42, result);
    }

    [Fact]
    public void Time_RunsTheVoidStepWhenDisabled()
    {
        var ran = false;

        SyncTrace.Time("label", () => ran = true);

        Assert.True(ran);
    }

    [Fact]
    public void Time_StillPropagatesTheStepsExceptionWhenDisabled()
    {
        Assert.Throws<InvalidOperationException>(() =>
            SyncTrace.Time<int>("label", () => throw new InvalidOperationException("boom")));
    }
}
