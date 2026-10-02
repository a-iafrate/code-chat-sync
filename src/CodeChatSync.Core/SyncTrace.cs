using System.Diagnostics;

namespace CodeChatSync.Core;

/// <summary>
/// Optional, opt-in trace of the sync pipeline's internal steps and their timing, for
/// diagnosing a run that is unexpectedly slow or appears to hang.
/// </summary>
/// <remarks>
/// <para>
/// Off by default and adds no measurable overhead when disabled — <see cref="IsEnabled"/>
/// is checked before any work happens — so it is safe to leave instrumented in hot paths
/// rather than ripped out after one investigation. Enabled by setting
/// <c>CODECHATSYNC_TRACE_LOG</c> to a file path before the sync runs; the CLI and the app
/// both pick it up with no other configuration. Never set by the test suite, so a test run
/// never writes outside its own temp directories.
/// </para>
/// <para>
/// A diagnostic log must never be what breaks the sync it is diagnosing: every write is
/// best-effort and swallows its own failures.
/// </para>
/// </remarks>
public static class SyncTrace
{
    private const string EnvironmentVariable = "CODECHATSYNC_TRACE_LOG";

    private static readonly object WriteLock = new();
    private static readonly object InitLock = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private static string? _path;
    private static bool _initialized;

    /// <summary>Whether a destination has been configured for this process.</summary>
    public static bool IsEnabled
    {
        get
        {
            EnsureInitialized();
            return _path is not null;
        }
    }

    /// <summary>Appends one timestamped line. A no-op when tracing is disabled.</summary>
    public static void Log(string message)
    {
        if (!IsEnabled)
        {
            return;
        }

        WriteLine(message);
    }

    /// <summary>
    /// Times a step, logging its start and either how long it took or the exception it
    /// threw. A no-op wrapper when tracing is disabled, so the step still runs.
    /// </summary>
    public static T Time<T>(string label, Func<T> step)
    {
        if (!IsEnabled)
        {
            return step();
        }

        WriteLine($"{label}: start");
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = step();
            WriteLine($"{label}: done in {Format(stopwatch.Elapsed)}");
            return result;
        }
        catch (Exception exception)
        {
            WriteLine($"{label}: FAILED after {Format(stopwatch.Elapsed)} — {exception.GetType().Name}: {exception.Message}");
            throw;
        }
    }

    /// <summary>Overload of <see cref="Time{T}"/> for a step with nothing to return.</summary>
    public static void Time(string label, Action step) =>
        Time<object?>(label, () =>
        {
            step();
            return null;
        });

    private static void WriteLine(string message)
    {
        var line = $"[{DateTimeOffset.Now:HH:mm:ss.fff} +{Format(Clock.Elapsed)}] [T{Environment.CurrentManagedThreadId}] {message}";
        try
        {
            lock (WriteLock)
            {
                File.AppendAllText(_path!, line + Environment.NewLine);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A diagnostic log failing to write must never be what breaks a sync.
        }
    }

    private static string Format(TimeSpan elapsed) => elapsed.ToString(@"mm\:ss\.fff");

    private static void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        lock (InitLock)
        {
            if (_initialized)
            {
                return;
            }

            var configured = Environment.GetEnvironmentVariable(EnvironmentVariable);
            if (configured is { Length: > 0 })
            {
                var fullPath = Path.GetFullPath(configured);
                var directory = Path.GetDirectoryName(fullPath);
                if (directory is { Length: > 0 })
                {
                    Directory.CreateDirectory(directory);
                }

                // Truncated here, once per process, rather than appended across runs: a
                // trace is read right after the run it was enabled for, and an
                // ever-growing file would make that run harder to find, not easier.
                File.WriteAllText(fullPath, string.Empty);
                _path = fullPath;
            }

            _initialized = true;
        }
    }
}
