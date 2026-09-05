using System.Diagnostics;
using AdoBoardSync.Core.Agents;
using AdoBoardSync.Core.Results;

namespace AdoBoardSync.Infrastructure.Agents;

/// <summary>
/// Which agent CLIs are installed on this machine (ABSD-701).
///
/// PATH is walked here rather than handed to a shell. A shell would parse the
/// provider's executable name as a command line, so a name carrying a
/// metacharacter would run something other than the binary it names; every child
/// this class starts is launched from the absolute path the walk returned, with
/// <c>UseShellExecute</c> off.
///
/// A provider that is not installed is simply not returned. "No agent CLIs
/// installed" is the ordinary state of a machine that has never used one — the
/// picker says so and the rest of the app is unaffected — whereas a failed
/// <see cref="Result{T}" /> would read as something being broken.
/// </summary>
public sealed class AgentProviderRegistry : IAgentProviderRegistry
{
    /// <summary>
    /// Long enough for a CLI that loads a language runtime before printing its
    /// version, short enough that the panel is not held open by one bad binary. A
    /// provider that has not answered by then is reported as absent rather than
    /// allowed to hang discovery.
    /// </summary>
    internal static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>A version banner longer than this is a binary that is not answering
    /// the question, and the picker is not the place to find that out.</summary>
    private const int MaxVersionLength = 120;

    private readonly IReadOnlyList<AgentProvider> _providers;
    private readonly string? _searchPath;
    private readonly TimeSpan _probeTimeout;

    public AgentProviderRegistry()
        : this(AgentProvider.Known, searchPath: null, probeTimeout: null)
    {
    }

    /// <summary>
    /// The seam the tests drive: a search path and a provider list of their own, so
    /// the suite proves this class against stub executables it created rather than
    /// against whichever agent CLIs the machine running it happens to have.
    /// </summary>
    internal AgentProviderRegistry(
        IReadOnlyList<AgentProvider> providers,
        string? searchPath,
        TimeSpan? probeTimeout)
    {
        _providers = providers;
        _searchPath = searchPath;
        _probeTimeout = probeTimeout ?? DefaultProbeTimeout;
    }

    public async Task<Result<IReadOnlyList<InstalledAgent>>> DiscoverAsync(
        CancellationToken cancellationToken = default)
    {
        var directories = ExecutableResolver.SearchDirectories(_searchPath);

        // Probed together rather than one after another: four cold starts of four
        // different language runtimes is a pause the user sees every time the agent
        // panel opens, and the probes have nothing to do with each other.
        var probes = _providers.Select(provider => ProbeAsync(provider, directories, cancellationToken));

        InstalledAgent?[] found;
        try
        {
            found = await Task.WhenAll(probes).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Caught rather than propagated: this port returns its failures, and a
            // caller that navigated away mid-probe should not receive an exception
            // through a Result-shaped seam.
            return Error.SourceFailure(
                "agent.discovery_cancelled", "Discovery of installed agent CLIs was cancelled.");
        }

        // The array preserves the provider order, which AgentProvider.Known documents
        // as the order the picker offers them in.
        return found.OfType<InstalledAgent>().ToList();
    }

    private async Task<InstalledAgent?> ProbeAsync(
        AgentProvider provider,
        IReadOnlyList<string> directories,
        CancellationToken cancellationToken)
    {
        var path = ExecutableResolver.Find(provider.Executable, directories);
        if (path is null)
        {
            return null;
        }

        var start = new ProcessStartInfo(path)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        start.ArgumentList.Add(provider.VersionArgument);

        // Discovery starts a child too, so it strips the token on the same terms the
        // runner does. The registry has no open profile and so no configured pat_env;
        // the default names are what it can strip, and they are the ones a machine set
        // up for this app actually exports.
        AgentEnvironment.StripPat(start.Environment, AgentEnvironment.PatVariableNames);

        using var timeout = new CancellationTokenSource(_probeTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        Process? process = null;
        Task<string>? standardOutput = null;
        Task<string>? standardError = null;
        try
        {
            process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            // Both pipes are drained while the wait runs. Reading one to the end first
            // deadlocks against a binary that fills the other.
            standardOutput = process.StandardOutput.ReadToEndAsync(linked.Token);
            standardError = process.StandardError.ReadToEndAsync(linked.Token);

            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);

            // A non-zero exit means this binary did not answer the version argument, so
            // it is not the CLI the provider names even though the file name matched.
            // Reporting it would put a provider in the picker that cannot be run.
            if (process.ExitCode != 0)
            {
                return null;
            }

            var version = FirstLine(await standardOutput.ConfigureAwait(false))
                ?? FirstLine(await standardError.ConfigureAwait(false));

            return new InstalledAgent(provider, path, version ?? "unknown version");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The probe timed out. Absent rather than an error: see the class comment.
            KillTree(process);
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or
                                       InvalidOperationException or PlatformNotSupportedException or
                                       UnauthorizedAccessException)
        {
            // The file matched but will not execute here — a shim that is not a real
            // image, a binary built for another architecture, a permission the walk
            // could not see. That is a provider this machine cannot run.
            return null;
        }
        finally
        {
            // A read can still be in flight when a timed-out probe disposes the
            // process. Observed here so a read that faults against a closed pipe is not
            // raised later on a thread that has nothing to do with discovery.
            Observe(standardOutput);
            Observe(standardError);
            process?.Dispose();
        }
    }

    /// <summary>The version, as the first line the binary printed that carried text.</summary>
    private static string? FirstLine(string output)
    {
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                return trimmed.Length > MaxVersionLength ? trimmed[..MaxVersionLength] : trimmed;
            }
        }

        return null;
    }

    private static void Observe(Task? task) => task?.ContinueWith(
        static completed => _ = completed.Exception,
        CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default);

    private static void KillTree(Process? process)
    {
        try
        {
            process?.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or
                                       System.ComponentModel.Win32Exception)
        {
            // It exited between the timeout and the kill, or the platform will not
            // enumerate its children. Either way there is nothing left to do.
        }
    }
}

/// <summary>Finds an executable on a search path without going through a shell.</summary>
internal static class ExecutableResolver
{
    /// <summary>The PATH entries, in order, from the given path or the environment's.</summary>
    internal static IReadOnlyList<string> SearchDirectories(string? searchPath)
    {
        var path = searchPath ?? Environment.GetEnvironmentVariable("PATH");
        return string.IsNullOrEmpty(path)
            ? []
            : path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>The absolute path of the first match, or null when nothing matches.</summary>
    internal static string? Find(string executable, IReadOnlyList<string> directories)
    {
        // A provider's executable is a bare file name by construction. This is the one
        // place a name could become a path, so a name carrying a separator is refused
        // outright rather than resolved against a PATH entry.
        if (executable.Length == 0 ||
            executable.Contains('/') ||
            executable.Contains('\\') ||
            executable.Contains(Path.DirectorySeparatorChar) ||
            executable.Contains(Path.AltDirectorySeparatorChar))
        {
            return null;
        }

        foreach (var directory in directories)
        {
            foreach (var name in Candidates(executable))
            {
                string candidate;
                try
                {
                    candidate = Path.Combine(directory, name);
                }
                catch (ArgumentException)
                {
                    // A PATH entry holding characters this platform will not accept in a
                    // path. Skip the entry rather than fail the whole walk.
                    break;
                }

                if (IsExecutableFile(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// On Windows a command is found through PATHEXT, so <c>claude</c> on PATH is
    /// really <c>claude.exe</c> or a <c>.cmd</c> shim. Elsewhere the name is the file
    /// name and nothing else.
    /// </summary>
    private static IEnumerable<string> Candidates(string executable)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [executable];
        }

        var pathExt = Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD";
        var names = new List<string> { executable };
        names.AddRange(
            pathExt
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(extension => executable + extension));
        return names;
    }

    private static bool IsExecutableFile(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            // Windows has no execute bit; PATHEXT already decided what is runnable.
            return true;
        }

        try
        {
            const UnixFileMode executable =
                UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            return (File.GetUnixFileMode(path) & executable) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       PlatformNotSupportedException)
        {
            return false;
        }
    }
}
