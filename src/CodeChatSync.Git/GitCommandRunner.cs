using System.Diagnostics;
using System.Text;

namespace CodeChatSync.Git;

/// <summary>Result of a single Git invocation.</summary>
public sealed record GitResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;

    /// <summary>The error output, falling back to standard output when Git stayed quiet.</summary>
    public string ErrorMessage =>
        StandardError.Length > 0 ? StandardError.Trim() : StandardOutput.Trim();
}

/// <summary>Whether the Git command line can be used, and which version was found.</summary>
public sealed record GitAvailability(bool IsAvailable, string? Version, string? Problem)
{
    /// <summary>Message explaining what to do when Git cannot be used.</summary>
    public string Description => IsAvailable
        ? Version ?? "git"
        : Problem ?? "The Git command line is not available.";
}

/// <summary>Raised when the Git command line is unavailable or a command fails.</summary>
public sealed class GitCommandException(string message) : Exception(message);

/// <summary>
/// Runs the installed <c>git</c> command line.
/// </summary>
/// <remarks>
/// Using the user's own Git installation keeps their existing credential helpers,
/// SSH keys and proxy settings working, and avoids embedding credentials anywhere
/// in this tool.
/// </remarks>
public sealed class GitCommandRunner(string? gitExecutable = null)
{
    private readonly string _gitExecutable = gitExecutable ?? "git";

    /// <summary>How long a single Git command may run before it is abandoned.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Checks that the Git command line can actually be invoked, so a missing or
    /// broken installation is reported up front rather than as a failure halfway
    /// through a sync.
    /// </summary>
    public GitAvailability CheckAvailability()
    {
        // Probed from a directory that always exists, so the result reflects Git
        // itself rather than the caller's current directory.
        var probeDirectory = Path.GetTempPath();

        GitResult result;
        try
        {
            result = new GitCommandRunner(_gitExecutable) { Timeout = TimeSpan.FromSeconds(30) }
                .Run(probeDirectory, ["--version"]);
        }
        catch (GitCommandException exception)
        {
            return new GitAvailability(false, null, exception.Message);
        }

        if (!result.Succeeded)
        {
            return new GitAvailability(
                false,
                null,
                $"'{_gitExecutable} --version' failed ({result.ExitCode}): {result.ErrorMessage}");
        }

        var version = result.StandardOutput.Trim();
        return new GitAvailability(true, version.Length == 0 ? null : version, null);
    }

    /// <summary>Throws with an actionable message when Git cannot be used.</summary>
    public void EnsureAvailable()
    {
        var availability = CheckAvailability();
        if (!availability.IsAvailable)
        {
            throw new GitCommandException(availability.Description);
        }
    }

    /// <summary>Runs a Git command in <paramref name="workingDirectory"/>.</summary>
    public GitResult Run(string workingDirectory, IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo
        {
            FileName = _gitExecutable,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Never let Git stop on an interactive prompt: a hung credential prompt would
        // block an unattended sync forever.
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (!Directory.Exists(workingDirectory))
        {
            throw new GitCommandException($"Working directory not found: {workingDirectory}");
        }

        using var process = new Process { StartInfo = startInfo };

        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();
        process.OutputDataReceived += (_, e) => Append(standardOutput, e.Data);
        process.ErrorDataReceived += (_, e) => Append(standardError, e.Data);

        try
        {
            process.Start();
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new GitCommandException(
                $"The Git command line ('{_gitExecutable}') could not be started. " +
                "Install Git and make sure it is on your PATH, then try again.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
        {
            TryKill(process);
            throw new GitCommandException(
                $"git {arguments.FirstOrDefault()} timed out after {Timeout.TotalMinutes:N0} minutes.");
        }

        // Lets the redirected streams finish flushing before the buffers are read.
        process.WaitForExit();

        return new GitResult(process.ExitCode, standardOutput.ToString(), standardError.ToString());
    }

    /// <summary>Runs a Git command and throws when it fails.</summary>
    public GitResult RunOrThrow(string workingDirectory, IReadOnlyList<string> arguments)
    {
        var result = Run(workingDirectory, arguments);
        if (!result.Succeeded)
        {
            throw new GitCommandException(
                $"git {string.Join(' ', arguments)} failed ({result.ExitCode}): {result.ErrorMessage}");
        }

        return result;
    }

    private static void Append(StringBuilder builder, string? line)
    {
        if (line is not null)
        {
            builder.AppendLine(line);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // The process already exited on its own.
        }
    }
}
