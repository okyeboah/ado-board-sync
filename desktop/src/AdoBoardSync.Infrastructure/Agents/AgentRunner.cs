using System.Diagnostics;
using System.Text;
using AdoBoardSync.Core.Agents;
using AdoBoardSync.Core.Configuration;
using AdoBoardSync.Core.Results;

namespace AdoBoardSync.Infrastructure.Agents;

/// <summary>
/// Runs a chosen agent CLI as a subprocess in the open profile's directory
/// (ABSD-702).
///
/// Three things about how the child is started are load-bearing rather than
/// incidental:
///
/// <list type="bullet">
/// <item>The board PAT is removed from the child's environment. See
/// <see cref="AgentEnvironment" /> — a process inherits its parent's environment, so
/// not adding the token would still hand it over.</item>
/// <item>The prompt goes to the child on stdin, and the argument list stays empty.
/// Arguments are visible to every other process on the machine through <c>ps</c>,
/// and a prompt can quote a backlog the user would not publish.</item>
/// <item>The binary is launched from the absolute path discovery resolved, with
/// <c>UseShellExecute</c> off, so nothing in the request is ever parsed as a
/// command.</item>
/// </list>
///
/// A run that starts and then fails is not an error: a non-zero exit, a timeout and
/// a cancellation are outcomes the user asked to see, and they come back as an
/// <see cref="AgentRunStatus" />. An <see cref="Error" /> means the run could not be
/// started at all.
/// </summary>
public sealed class AgentRunner : IAgentRunner
{
    /// <summary>
    /// Output beyond this is dropped from the captured result. Everything is still
    /// streamed to the caller as it arrives; this bounds only what is held in memory
    /// afterwards, because an agent that loops printing must not grow the desktop's
    /// heap until it is killed.
    /// </summary>
    private const int MaxCapturedCharacters = 1024 * 1024;

    /// <summary>How long to wait for a killed process to actually go away before
    /// giving up on draining its pipes and reporting the outcome anyway.</summary>
    private static readonly TimeSpan KillGrace = TimeSpan.FromSeconds(5);

    private readonly IReadOnlyList<string> _patVariableNames;

    public AgentRunner()
        : this(null)
    {
    }

    /// <param name="additionalPatVariableNames">
    /// Names to strip beyond the defaults — the open profile's <c>pat_env</c>, which
    /// a project is free to rename.
    /// </param>
    public AgentRunner(IEnumerable<string>? additionalPatVariableNames)
    {
        var names = new List<string>(AgentEnvironment.PatVariableNames);
        if (additionalPatVariableNames is not null)
        {
            names.AddRange(additionalPatVariableNames);
        }

        _patVariableNames = names;
    }

    /// <summary>
    /// A runner for one profile, stripping the variable that profile's
    /// <c>pat_env</c> names as well as the defaults. Mirrors
    /// <see cref="PatResolver.ForConfig" />: the same setting decides where the token
    /// is read from and where it must not be passed on to.
    /// </summary>
    public static AgentRunner ForConfig(BoardConfig config) => new([config.PatEnv]);

    /// <summary>The variable names this runner removes from a child's environment.</summary>
    public IReadOnlyList<string> PatVariableNames => _patVariableNames;

    public async Task<Result<AgentRunResult>> RunAsync(
        AgentRunRequest request,
        IProgress<string>? output = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            return Error.Validation("agent.prompt_empty", "An agent run needs a prompt.");
        }

        if (request.Timeout <= TimeSpan.Zero)
        {
            // Refused here rather than let CancellationTokenSource throw it back
            // through a seam every caller treats as total.
            return Error.Validation("agent.timeout_invalid", "An agent run needs a timeout greater than zero.");
        }

        if (!Directory.Exists(request.WorkingDirectory))
        {
            return Error.NotFound(
                "agent.working_directory_missing",
                $"The profile directory {request.WorkingDirectory} no longer exists.");
        }

        var executable = request.Agent.ExecutablePath;
        if (!File.Exists(executable))
        {
            // Discovery ran when the panel opened; the user may have uninstalled the
            // CLI since. Named plainly, because "it worked this morning" is exactly
            // what the user will be thinking.
            return Error.NotFound(
                "agent.executable_missing",
                $"{request.Agent.Provider.DisplayName} is no longer installed at {executable}.");
        }

        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = request.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // The requirement this class exists to meet. ProcessStartInfo.Environment is
        // pre-populated from this process, so a desktop launched from a shell that
        // exported the PAT hands it to the agent unless the name is removed here. The
        // argument list stays empty and the prompt goes on stdin, so the token has no
        // other route to the child either.
        AgentEnvironment.StripPat(start.Environment, _patVariableNames);

        var captured = new CapturedOutput(output);
        var capturedErrors = new CapturedOutput(null);
        var clock = Stopwatch.StartNew();

        using var process = new Process { StartInfo = start };

        // Streamed line by line rather than read to the end: an agent run lasts
        // minutes, and a panel that shows nothing until it finishes is indistinguishable
        // from one that has hung.
        process.OutputDataReceived += (_, e) => captured.Add(e.Data);
        process.ErrorDataReceived += (_, e) => capturedErrors.Add(e.Data);

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or
                                       InvalidOperationException or PlatformNotSupportedException or
                                       UnauthorizedAccessException)
        {
            return Error.SourceFailure(
                "agent.start_failed",
                $"Could not start {request.Agent.Provider.DisplayName}: {ex.Message}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = new CancellationTokenSource(request.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            await WritePromptAsync(process, request.Prompt, linked.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var status = cancellationToken.IsCancellationRequested
                ? AgentRunStatus.Cancelled
                : AgentRunStatus.TimedOut;

            await StopAsync(process).ConfigureAwait(false);
            return Finish(status, ExitCodeOf(process), captured, capturedErrors, clock);
        }

        var exitCode = ExitCodeOf(process);
        var outcome = exitCode == 0 ? AgentRunStatus.Succeeded : AgentRunStatus.Failed;
        return Finish(outcome, exitCode, captured, capturedErrors, clock);
    }

    /// <summary>
    /// Sends the prompt and closes stdin, so a CLI reading until end-of-input knows
    /// the prompt is complete.
    /// </summary>
    private static async Task WritePromptAsync(Process process, string prompt, CancellationToken cancellationToken)
    {
        try
        {
            // Cancellable, because a child that never reads its stdin fills the pipe
            // buffer and this write would otherwise outlive the run's own timeout.
            await process.StandardInput.WriteAsync(prompt.AsMemory(), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.WriteAsync("\n".AsMemory(), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The child exited or closed stdin before reading the prompt. Its exit
            // code is the answer the user needs, not this.
        }
        finally
        {
            try
            {
                process.StandardInput.Close();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // Already gone with the process.
            }
        }
    }

    /// <summary>Ends a run that timed out or was cancelled, and waits for its output.</summary>
    private static async Task StopAsync(Process process)
    {
        try
        {
            // The whole tree, not just the child. An agent CLI spawns its own children
            // — a language runtime, an MCP server, a git process — and killing only the
            // parent orphans them: they keep running against the profile directory the
            // user has just cancelled work in, and they keep the output pipe open so
            // this method would never return.
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or
                                       System.ComponentModel.Win32Exception)
        {
            // It exited between the timeout firing and the kill, or the platform will
            // not enumerate its children. Fall through and collect what it printed.
        }

        try
        {
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(KillGrace).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
        {
            // Something in the tree survived the kill and still holds the pipe. Report
            // the outcome with the output collected so far rather than wait on it.
        }
    }

    private static int ExitCodeOf(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            // Still running after a kill that did not take, or never associated.
            return -1;
        }
    }

    private static AgentRunResult Finish(
        AgentRunStatus status,
        int exitCode,
        CapturedOutput output,
        CapturedOutput errors,
        Stopwatch clock) =>
        new()
        {
            Status = status,
            ExitCode = exitCode,
            StandardOutput = output.Text,
            StandardError = errors.Text,
            Duration = clock.Elapsed,
        };

    /// <summary>
    /// One redirected stream: reported to the caller as each line arrives, and kept
    /// up to a bound for the result.
    /// </summary>
    private sealed class CapturedOutput(IProgress<string>? progress)
    {
        private readonly StringBuilder _text = new();
        private readonly Lock _gate = new();
        private bool _truncated;

        internal string Text
        {
            get
            {
                lock (_gate)
                {
                    return _text.ToString();
                }
            }
        }

        internal void Add(string? line)
        {
            // A null line is the end of the stream, not an empty one.
            if (line is null)
            {
                return;
            }

            lock (_gate)
            {
                if (_text.Length + line.Length + 1 <= MaxCapturedCharacters)
                {
                    _text.Append(line).Append('\n');
                }
                else if (!_truncated)
                {
                    _truncated = true;
                    _text.Append("\n[output truncated]\n");
                }
            }

            progress?.Report(line);
        }
    }
}
