using System.Diagnostics;
using AdoBoardSync.Core.Agents;
using AdoBoardSync.Core.Configuration;
using AdoBoardSync.Infrastructure.Agents;
using AdoBoardSync.TestKit;

namespace AdoBoardSync.Desktop.Tests;

/// <summary>
/// Running a chosen agent CLI (ABSD-702).
///
/// The subject is a stub script the test wrote, not an installed agent: the CI
/// runner has none, and no real CLI would let this suite assert what a child sees
/// in its environment, on its stdin, and in its argument list — which is the whole
/// of what ABSD-702 promises.
/// </summary>
public sealed class AgentRunnerTests
{
    [PosixFact]
    public async Task ThePatIsRemovedFromTheChildEnvironment()
    {
        using var stubs = new AgentStubs();
        var patVariable = "ABSD_TEST_PAT_" + Guid.NewGuid().ToString("N");
        var controlVariable = "ABSD_TEST_CONTROL_" + Guid.NewGuid().ToString("N");
        const string secret = "pat-value-that-must-not-escape";

        // Set on this process, which is the situation the runner has to survive: a
        // desktop started from a shell that exported the token would pass it on unless
        // the variable is actively removed from the child's environment.
        Environment.SetEnvironmentVariable(patVariable, secret);
        Environment.SetEnvironmentVariable(controlVariable, "inherited");
        try
        {
            var stub = stubs.Write("stubagent", "env");
            var config = InMemoryConfig.Create(customise: document => document["pat_env"] = patVariable);

            var result = await AgentRunner.ForConfig(config).RunAsync(Request(stub, stubs.Root));

            var environment = result.Value.StandardOutput;

            // The control variable proves the child inherited an environment at all, so
            // the token's absence is this runner removing it rather than a child that
            // was handed nothing.
            Assert.Contains(controlVariable + "=inherited", environment, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, environment, StringComparison.Ordinal);
            Assert.DoesNotContain(patVariable, environment, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(patVariable, null);
            Environment.SetEnvironmentVariable(controlVariable, null);
        }
    }

    [Fact]
    public void TheStrippedNamesCoverTheConfiguredVariableAndItsCommonAliases()
    {
        var config = InMemoryConfig.Create(customise: document => document["pat_env"] = "MY_BOARD_TOKEN");

        var names = AgentRunner.ForConfig(config).PatVariableNames;

        Assert.Contains("MY_BOARD_TOKEN", names);
        Assert.Contains(BoardConfig.DefaultPatEnv, names);
        Assert.Contains("AZURE_DEVOPS_EXT_PAT", names);
        Assert.Contains("SYSTEM_ACCESSTOKEN", names);
    }

    [PosixFact]
    public async Task ThePromptArrivesOnStdinAndNeverInTheArguments()
    {
        using var stubs = new AgentStubs();
        var stub = stubs.Write("stubagent", "echo \"args=[$*]\"\necho \"stdin=[$(cat)]\"");
        const string prompt = "Rewrite ABSD-401 as three tasks.";

        var result = await new AgentRunner().RunAsync(Request(stub, stubs.Root, prompt));

        Assert.Equal(AgentRunStatus.Succeeded, result.Value.Status);
        Assert.Equal(0, result.Value.ExitCode);
        Assert.Contains("stdin=[" + prompt + "]", result.Value.StandardOutput, StringComparison.Ordinal);

        // An argument is readable by every other process on the machine through ps,
        // and a prompt quotes a backlog the user may not have published.
        Assert.Contains("args=[]", result.Value.StandardOutput, StringComparison.Ordinal);
    }

    [PosixFact]
    public async Task OutputIsReportedWhileTheAgentIsStillRunning()
    {
        using var stubs = new AgentStubs();
        var stub = stubs.Write("stubagent", "echo first\nsleep 2\necho second");
        var progress = new RecordingProgress();

        var result = await new AgentRunner().RunAsync(Request(stub, stubs.Root), progress);

        Assert.Equal(["first", "second"], progress.Lines.Select(line => line.Text));

        // A run buffered to the end would report both lines at once, two seconds in,
        // which is indistinguishable from a hang for as long as the agent is thinking.
        Assert.True(
            progress.Lines[0].At < TimeSpan.FromSeconds(1.5),
            $"the first line was reported after {progress.Lines[0].At}");
        Assert.True(result.Value.Duration > TimeSpan.FromSeconds(1.5), $"the run took {result.Value.Duration}");
    }

    [PosixFact]
    public async Task ATimeoutEndsTheRunAndKillsTheChildrenTheAgentSpawned()
    {
        using var stubs = new AgentStubs();
        var orphanEvidence = Path.Combine(stubs.Root, "orphan-kept-running");
        var stub = stubs.Write(
            "stubagent",
            $"( sleep 4; touch '{orphanEvidence}' ) &\necho started\nsleep 60");

        var clock = Stopwatch.StartNew();
        var result = await new AgentRunner()
            .RunAsync(Request(stub, stubs.Root, timeout: TimeSpan.FromSeconds(1)));
        clock.Stop();

        Assert.Equal(AgentRunStatus.TimedOut, result.Value.Status);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"the run took {clock.Elapsed}");

        // Long enough for the grandchild to have written its marker had the kill only
        // reached the shell it was spawned from.
        await Task.Delay(TimeSpan.FromSeconds(6));
        Assert.False(File.Exists(orphanEvidence), "a process the agent spawned outlived the timeout");
    }

    [PosixFact]
    public async Task CancellationEndsTheRunAsCancelledRatherThanTimedOut()
    {
        using var stubs = new AgentStubs();
        var stub = stubs.Write("stubagent", "sleep 60");
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var clock = Stopwatch.StartNew();
        var result = await new AgentRunner().RunAsync(
            Request(stub, stubs.Root, timeout: TimeSpan.FromMinutes(5)), output: null, caller.Token);
        clock.Stop();

        Assert.Equal(AgentRunStatus.Cancelled, result.Value.Status);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"the run took {clock.Elapsed}");
    }

    [PosixFact]
    public async Task ANonZeroExitIsReportedAsFailedRatherThanThrown()
    {
        using var stubs = new AgentStubs();
        var stub = stubs.Write("stubagent", "echo working\necho 'it broke' >&2\nexit 3");

        var result = await new AgentRunner().RunAsync(Request(stub, stubs.Root));

        Assert.True(result.IsSuccess, "a run that started and failed is an outcome, not an error");
        Assert.Equal(AgentRunStatus.Failed, result.Value.Status);
        Assert.Equal(3, result.Value.ExitCode);
        Assert.Contains("working", result.Value.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("it broke", result.Value.StandardError, StringComparison.Ordinal);
    }

    [PosixFact]
    public async Task TheAgentRunsInTheProfileDirectory()
    {
        using var stubs = new AgentStubs();
        using var profile = new AgentStubs();
        File.WriteAllText(Path.Combine(profile.Root, "backlog.md"), "# Backlog\n");
        var stub = stubs.Write("stubagent", "if [ -f backlog.md ]; then echo in-profile; else echo elsewhere; fi");

        var result = await new AgentRunner().RunAsync(Request(stub, profile.Root));

        Assert.Contains("in-profile", result.Value.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnExecutableThatDisappearedAfterDiscoveryIsATypedError()
    {
        using var stubs = new AgentStubs();

        var result = await new AgentRunner()
            .RunAsync(Request(Path.Combine(stubs.Root, "uninstalled"), stubs.Root));

        Assert.True(result.IsFailure);
        Assert.Equal("agent.executable_missing", result.Error!.Code);
    }

    [Fact]
    public async Task AProfileDirectoryThatIsGoneIsATypedError()
    {
        using var stubs = new AgentStubs();

        var result = await new AgentRunner()
            .RunAsync(Request(Path.Combine(stubs.Root, "stubagent"), Path.Combine(stubs.Root, "moved")));

        Assert.Equal("agent.working_directory_missing", result.Error?.Code);
    }

    [Fact]
    public async Task AnEmptyPromptIsRefusedBeforeAnythingIsStarted()
    {
        using var stubs = new AgentStubs();

        var result = await new AgentRunner().RunAsync(Request(stubs.Root, stubs.Root, prompt: "   "));

        Assert.Equal("agent.prompt_empty", result.Error?.Code);
    }

    [Fact]
    public async Task ATimeoutOfZeroIsRefusedRatherThanThrown()
    {
        using var stubs = new AgentStubs();

        var result = await new AgentRunner()
            .RunAsync(Request(stubs.Root, stubs.Root, timeout: TimeSpan.Zero));

        Assert.Equal("agent.timeout_invalid", result.Error?.Code);
    }

    private static AgentRunRequest Request(
        string executablePath,
        string workingDirectory,
        string prompt = "Draft ABSD-999 as an Issue.",
        TimeSpan? timeout = null) =>
        new()
        {
            Agent = new InstalledAgent(
                new AgentProvider("stub", "Stub Agent", "stubagent", "--version"), executablePath, "1.0"),
            Prompt = prompt,
            WorkingDirectory = workingDirectory,
            Scope = AgentScope.Backlog,
            Timeout = timeout ?? TimeSpan.FromSeconds(30),
        };

    /// <summary>
    /// Records what the runner reported and when. Deliberately not
    /// <see cref="Progress{T}" />, which posts asynchronously and would time the
    /// scheduler rather than the run.
    /// </summary>
    private sealed class RecordingProgress : IProgress<string>
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Lock _gate = new();
        private readonly List<(string Text, TimeSpan At)> _lines = [];

        internal IReadOnlyList<(string Text, TimeSpan At)> Lines
        {
            get
            {
                lock (_gate)
                {
                    return _lines.ToList();
                }
            }
        }

        public void Report(string value)
        {
            lock (_gate)
            {
                _lines.Add((value, _clock.Elapsed));
            }
        }
    }
}
