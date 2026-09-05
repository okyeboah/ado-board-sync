using System.Diagnostics;
using AdoBoardSync.Core.Agents;
using AdoBoardSync.Infrastructure.Agents;

namespace AdoBoardSync.Desktop.Tests;

/// <summary>
/// Discovery of installed agent CLIs (ABSD-701).
///
/// Nothing here depends on <c>claude</c>, <c>codex</c>, <c>opencode</c> or
/// <c>gemini</c> being installed — the CI runner has none of them, and a suite that
/// needed them would be green or red for reasons that have nothing to do with this
/// code. The registry is driven against stub scripts on a search path the test
/// created, which is also the only way to assert what it does with a binary that
/// hangs or exits non-zero.
/// </summary>
public sealed class AgentProviderRegistryTests
{
    [PosixFact]
    public async Task AnInstalledProviderIsReportedWithTheVersionItPrinted()
    {
        using var stubs = new AgentStubs();
        var path = stubs.Write("stubagent", "echo 'stubagent 1.4.2'");

        var found = await Discover(stubs, Provider("stubagent"));

        var agent = Assert.Single(found);
        Assert.Equal("stubagent 1.4.2", agent.Version);
        Assert.Equal(path, agent.ExecutablePath);
        Assert.Equal("Stub Agent stubagent 1.4.2", agent.Display);
    }

    [PosixFact]
    public async Task AProviderThatIsNotInstalledIsSimplyAbsent()
    {
        using var stubs = new AgentStubs();

        var result = await new AgentProviderRegistry([Provider("stubagent")], stubs.Root, ProbeTimeout)
            .DiscoverAsync();

        // Not an error: a machine that has never used an agent CLI is ordinary, and a
        // failed Result would make the whole panel read as broken.
        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value);
    }

    [PosixFact]
    public async Task ProvidersAreReportedInTheOrderThePickerOffersThem()
    {
        using var stubs = new AgentStubs();
        stubs.Write("first", "echo 1.0");
        stubs.Write("second", "echo 2.0");
        stubs.Write("third", "echo 3.0");

        var found = await Discover(stubs, Provider("third"), Provider("first"), Provider("second"));

        Assert.Equal(["third", "first", "second"], found.Select(agent => agent.Provider.Executable));
    }

    [PosixFact]
    public async Task ABinaryThatFailsItsVersionArgumentIsNotReported()
    {
        using var stubs = new AgentStubs();
        stubs.Write("stubagent", "echo 'unknown option' >&2; exit 2");

        Assert.Empty(await Discover(stubs, Provider("stubagent")));
    }

    [PosixFact]
    public async Task AVersionPrintedOnStandardErrorIsStillReported()
    {
        using var stubs = new AgentStubs();
        stubs.Write("stubagent", "echo 'stubagent 0.9' >&2");

        Assert.Equal("stubagent 0.9", Assert.Single(await Discover(stubs, Provider("stubagent"))).Version);
    }

    [PosixFact]
    public async Task AHangingBinaryDoesNotHangDiscovery()
    {
        using var stubs = new AgentStubs();
        stubs.Write("stubagent", "sleep 60");

        var clock = Stopwatch.StartNew();
        var found = await Discover(stubs, Provider("stubagent"));
        clock.Stop();

        Assert.Empty(found);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"discovery took {clock.Elapsed}");
    }

    [PosixFact]
    public async Task TheProbesRunConcurrently()
    {
        using var stubs = new AgentStubs();

        // The three stubs rendezvous: each announces itself and then reports a version
        // only once all three have. Asserted this way rather than by timing the run,
        // because a wall clock on a loaded machine measures the machine. A registry
        // that probed one provider after another would leave the first stub waiting for
        // two that had not been started, and would report nothing at all.
        foreach (var name in new[] { "one", "two", "three" })
        {
            stubs.Write(
                "rendezvous" + name,
                $"""
                 touch '{stubs.Root}/started-{name}'
                 attempt=0
                 while [ $attempt -lt 15 ]; do
                     set -- '{stubs.Root}'/started-*
                     if [ $# -ge 3 ]; then echo 'rendezvous 1.0'; exit 0; fi
                     sleep 1
                     attempt=$((attempt + 1))
                 done
                 exit 1
                 """);
        }

        var registry = new AgentProviderRegistry(
            [Provider("rendezvousone"), Provider("rendezvoustwo"), Provider("rendezvousthree")],
            stubs.Root,
            TimeSpan.FromSeconds(60));

        var result = await registry.DiscoverAsync();

        Assert.Equal(3, result.Value.Count);
    }

    [PosixFact]
    public async Task AnExecutableNameIsNeverInterpretedAsACommand()
    {
        using var stubs = new AgentStubs();
        stubs.Write("victim", "echo 1.0");
        var evidence = Path.Combine(stubs.Root, "shell-ran");

        // A shell would read this as two commands. The registry resolves file names on
        // PATH itself and starts the binary directly, so it resolves nothing at all.
        var found = await Discover(stubs, Provider($"victim; touch {evidence}"));

        Assert.Empty(found);
        Assert.False(File.Exists(evidence));
    }

    [PosixFact]
    public async Task AnExecutableNameCarryingAPathIsNeverResolved()
    {
        using var stubs = new AgentStubs();
        stubs.Write("victim", "echo 1.0");

        Assert.Empty(await Discover(stubs, Provider("../" + Path.GetFileName(stubs.Root) + "/victim")));
    }

    [PosixFact]
    public async Task AFileWithoutTheExecuteBitIsNotAProvider()
    {
        using var stubs = new AgentStubs();
        stubs.WriteUnrunnable("stubagent", "echo 1.0");

        Assert.Empty(await Discover(stubs, Provider("stubagent")));
    }

    [PosixFact]
    public async Task TheFirstMatchOnTheSearchPathWins()
    {
        using var stubs = new AgentStubs();
        using var shadowed = new AgentStubs();
        var winner = stubs.Write("stubagent", "echo 'front 1.0'");
        shadowed.Write("stubagent", "echo 'back 2.0'");

        var registry = new AgentProviderRegistry(
            [Provider("stubagent")],
            stubs.Root + Path.PathSeparator + shadowed.Root,
            ProbeTimeout);

        var agent = Assert.Single((await registry.DiscoverAsync()).Value);
        Assert.Equal(winner, agent.ExecutablePath);
        Assert.Equal("front 1.0", agent.Version);
    }

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    private static AgentProvider Provider(string executable) =>
        new(executable, "Stub Agent", executable, "--version");

    private static async Task<IReadOnlyList<InstalledAgent>> Discover(
        AgentStubs stubs,
        params AgentProvider[] providers)
    {
        var registry = new AgentProviderRegistry(providers, stubs.Root, ProbeTimeout);
        var result = await registry.DiscoverAsync();
        Assert.True(result.IsSuccess, result.Error?.Code);
        return result.Value;
    }
}

/// <summary>
/// Skips where the stub agents cannot run. They are <c>/bin/sh</c> scripts because
/// that is the shortest way to write a binary that hangs, exits non-zero, or prints
/// its own environment — the three things ABSD-701 and ABSD-702 have to be proved
/// against — and a Windows runner has no interpreter for them.
/// </summary>
public sealed class PosixFactAttribute : FactAttribute
{
    public PosixFactAttribute()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            Skip = "The agent stubs are /bin/sh scripts, so these tests need a POSIX platform.";
        }
    }
}

/// <summary>A throwaway directory of stub agent executables, used as a search path.</summary>
internal sealed class AgentStubs : IDisposable
{
    internal AgentStubs()
    {
        Root = Path.Combine(Path.GetTempPath(), "absd-agents-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    internal string Root { get; }

    /// <summary>Writes a runnable stub and returns the path discovery should report.</summary>
    internal string Write(string name, string script)
    {
        var path = WriteUnrunnable(name, script);

        // Guarded, not conditional behaviour: every caller is a PosixFact, and the
        // guard is what lets this file compile on a Windows runner where the tests
        // that use it are skipped.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    /// <summary>Writes the same script without the execute bit.</summary>
    internal string WriteUnrunnable(string name, string script)
    {
        var path = Path.Combine(Root, name);
        File.WriteAllText(path, "#!/bin/sh\n" + script + "\n");
        return Path.GetFullPath(path);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A stub a killed process still holds open. The temp directory is the
            // operating system's to clean up, and a failure here would hide the
            // assertion that actually mattered.
        }
    }
}
