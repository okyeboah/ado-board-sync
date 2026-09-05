using System.Text;
using AdoBoardSync.Core.Agents;
using AdoBoardSync.Core.Backlog;
using AdoBoardSync.Core.Configuration;
using AdoBoardSync.Core.Operations;
using AdoBoardSync.Core.Results;
using AdoBoardSync.Desktop.Services;
using AdoBoardSync.Desktop.ViewModels;

namespace AdoBoardSync.Desktop.Tests;

/// <summary>
/// The agent-authoring surface (ABSD-703, ABSD-705).
///
/// <see cref="AgentEditSessionTests" /> pins what happens to the file; this pins
/// what the person deciding sees. Two things carry the weight. The three
/// disclosure lines must describe the run that is about to happen — a sentence
/// naming the previous provider or the previous scope is worse than none, because
/// it is read as a promise. And accepting an edit must not shorten the path to the
/// board: the surface can ask the shell to open the Plan, and that is all it can
/// do (ABSD-705).
/// </summary>
public class AgentAuthoringViewModelTests
{
    private const string Original = """
        ## Epic 1: Foundation

        Epic body.

        ### PROJ-101 Â· First issue

        First body.

        - Do the first thing
        """;

    private const string Edited = """
        ## Epic 1: Foundation

        Epic body.

        ### PROJ-101 Â· First issue

        First body.

        - Do the first thing
        - Do the second thing
        """;

    private static BoardConfig Config() =>
        BoardConfig.Parse(
            """{"org":"acme","project":"widgets","code_prefix":"PROJ","board_file":"backlog.md"}""",
            Path.GetTempPath()).Value;

    private static BacklogWorkspace Workspace(string markdown = Original)
    {
        var config = Config();
        return new BacklogWorkspace(
            null, config, Path.Combine(Path.GetTempPath(), "backlog.md"), markdown,
            BacklogParser.Parse(config, markdown), 0,
            FileStamp.For(DateTimeOffset.UnixEpoch, markdown));
    }

    private static InstalledAgent Agent(int index = 0) =>
        new(AgentProvider.Known[index], "/usr/local/bin/agent", "1.2.3");

    // ------------------------------------------------------------- the fakes

    private sealed class FakeRegistry : IAgentProviderRegistry
    {
        public InstalledAgent[] Installed { get; set; } = [];

        public Error? Failure { get; set; }

        public Task<Result<IReadOnlyList<InstalledAgent>>> DiscoverAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Result<IReadOnlyList<InstalledAgent>>>(
                Failure is { } error ? error : Installed);
    }

    private sealed class FakeFileStore : IAgentEditFileStore
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

        public Error? WriteError { get; set; }

        public FakeFileStore Seed(string path, string text)
        {
            _files[path] = Encoding.UTF8.GetBytes(text);
            return this;
        }

        public string Text(string path) => Encoding.UTF8.GetString(_files[path]);

        public Result<byte[]> ReadBytes(string path) =>
            _files.TryGetValue(path, out var bytes)
                ? bytes
                : Error.NotFound("agent.edit.not_found", $"File not found: {path}.");

        public Result<bool> WriteBytes(string path, byte[] bytes)
        {
            if (WriteError is { } error)
            {
                return error;
            }

            _files[path] = bytes;
            return true;
        }
    }

    private sealed class FakeRunner(
        FakeFileStore files, string path, string? writes, AgentRunStatus status = AgentRunStatus.Succeeded)
        : IAgentRunner
    {
        public List<AgentRunRequest> Requests { get; } = [];

        public Task<Result<AgentRunResult>> RunAsync(
            AgentRunRequest request, IProgress<string>? output = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);

            if (writes is not null)
            {
                files.WriteBytes(path, Encoding.UTF8.GetBytes(writes));
            }

            output?.Report("working…");

            return Task.FromResult<Result<AgentRunResult>>(new AgentRunResult
            {
                Status = status,
                ExitCode = status == AgentRunStatus.Succeeded ? 0 : 1,
                StandardOutput = "working…",
            });
        }
    }

    private sealed class FakeAgentHistory : IAgentRunHistory
    {
        private long _next = 1;

        public List<AgentRunRecord> Records { get; } = [];

        public List<(long Id, bool Accepted)> Verdicts { get; } = [];

        public Task<Result<long>> RecordRunAsync(
            AgentRunRecord record, CancellationToken cancellationToken = default)
        {
            var id = _next++;
            Records.Add(record with { Id = id });
            return Task.FromResult<Result<long>>(id);
        }

        public Task<Result<bool>> RecordVerdictAsync(
            long runId, bool accepted, DateTimeOffset finishedAt, CancellationToken cancellationToken = default)
        {
            Verdicts.Add((runId, accepted));
            return Task.FromResult<Result<bool>>(true);
        }

        public Task<Result<IReadOnlyList<AgentRunRecord>>> ListRunsAsync(
            string profileKey, int limit, CancellationToken cancellationToken = default)
        {
            AgentRunRecord[] matching = [.. Records.Where(r => r.ProfileKey == profileKey).Take(limit)];
            return Task.FromResult<Result<IReadOnlyList<AgentRunRecord>>>(matching);
        }
    }

    private sealed record Subject(
        AgentAuthoringViewModel Model,
        FakeFileStore Files,
        FakeAgentHistory History,
        FakeRegistry Registry,
        BacklogWorkspace Workspace);

    private static Subject Build(
        string? agentWrites = Edited,
        AgentRunStatus status = AgentRunStatus.Succeeded,
        InstalledAgent[]? installed = null)
    {
        var workspace = Workspace();
        var files = new FakeFileStore().Seed(workspace.BacklogPath, Original);
        var history = new FakeAgentHistory();
        var session = new AgentEditSession(
            new FakeRunner(files, workspace.BacklogPath, agentWrites, status), files, history);
        var registry = new FakeRegistry { Installed = installed ?? [Agent()] };

        return new Subject(
            new AgentAuthoringViewModel(session, registry) { Workspace = workspace },
            files, history, registry, workspace);
    }

    /// <summary>
    /// Waits for the agent's streamed output to arrive.
    ///
    /// <see cref="Progress{T}" /> delivers on the SynchronizationContext captured
    /// when it was constructed. Under Avalonia that is the dispatcher, so a line
    /// reported during the run is on screen by the time the run returns. A test has
    /// no context, so delivery is posted to the thread pool and lands some time
    /// after — asserting on it synchronously passes or fails with the scheduler.
    /// </summary>
    private static async Task<IReadOnlyList<string>> OutputAsync(
        AgentAuthoringViewModel model, int expected = 1)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (model.Output.Count < expected && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5);
        }

        return [.. model.Output];
    }

    private static async Task<Subject> ReadyAsync(
        string? agentWrites = Edited,
        AgentRunStatus status = AgentRunStatus.Succeeded)
    {
        var subject = Build(agentWrites, status);
        await subject.Model.DiscoverAsync();
        subject.Model.Prompt = "Add a second task to PROJ-101.";
        return subject;
    }

    // ---------------------------------------------------------- discovery

    [Fact]
    public async Task DiscoveryOffersTheInstalledAgentsAndSelectsTheFirst()
    {
        var subject = Build(installed: [Agent(0), Agent(1)]);

        await subject.Model.DiscoverAsync();

        Assert.Equal(2, subject.Model.Providers.Count);
        Assert.Equal(subject.Model.Providers[0], subject.Model.SelectedProvider);
        Assert.Equal("2 agent CLIs found.", subject.Model.StatusText);
    }

    [Fact]
    public async Task AMachineWithNoAgentSaysSoAndCannotRun()
    {
        var subject = Build(installed: []);

        await subject.Model.DiscoverAsync();
        subject.Model.Prompt = "Do something.";

        Assert.False(subject.Model.HasProvider);
        Assert.False(subject.Model.CanRun);
        Assert.Contains("No agent CLI was found", subject.Model.ProviderStatement, StringComparison.Ordinal);
        Assert.Contains("Install Claude Code", subject.Model.ProviderStatement, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedProbeIsReportedWithItsCode()
    {
        var subject = Build();
        subject.Registry.Failure = Error.SourceFailure("agent.probe_failed", "PATH is not readable");

        await subject.Model.DiscoverAsync();

        Assert.True(subject.Model.HasError);
        Assert.Contains("agent.probe_failed", subject.Model.ErrorText);
    }

    // -------------------------------------------------- the three statements

    [Fact]
    public async Task TheProviderStatementNamesTheBinaryAndDisclaimsItsCredentials()
    {
        var subject = await ReadyAsync();

        Assert.Contains("/usr/local/bin/agent", subject.Model.ProviderStatement, StringComparison.Ordinal);
        Assert.Contains("1.2.3", subject.Model.ProviderStatement, StringComparison.Ordinal);
        Assert.Contains("this app holds none of them", subject.Model.ProviderStatement, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReadStatementNamesTheDirectoryAndSaysTheTokenIsRemoved()
    {
        var subject = Build();

        Assert.Contains(
            subject.Workspace.Config.BaseDirectory, subject.Model.ReadStatement, StringComparison.Ordinal);
        Assert.Contains("token is removed", subject.Model.ReadStatement, StringComparison.Ordinal);
    }

    [Fact]
    public void TheChangeStatementNamesTheFileAndRulesOutTheBoard()
    {
        // The one that is easiest to get wrong, and the one a user is relying on
        // when they hand a local CLI a directory.
        var subject = Build();

        Assert.Contains(subject.Workspace.BacklogPath, subject.Model.ChangeStatement, StringComparison.Ordinal);
        Assert.Contains("nothing reaches Azure DevOps", subject.Model.ChangeStatement, StringComparison.Ordinal);
        Assert.Contains("Plan and Apply", subject.Model.ChangeStatement, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryStatementReadsSensiblyBeforeAProfileIsOpen()
    {
        // The surface is reachable before a profile is open, and a statement that
        // interpolated a null path would be the first thing a new user reads.
        var model = new AgentAuthoringViewModel(
            new AgentEditSession(
                new FakeRunner(new FakeFileStore(), "x", null), new FakeFileStore(), new FakeAgentHistory()),
            new FakeRegistry());

        Assert.All(
            new[] { model.ProviderStatement, model.ReadStatement, model.ChangeStatement, model.ScopeStatement },
            statement =>
            {
                Assert.NotEmpty(statement);
                Assert.DoesNotContain("null", statement, StringComparison.OrdinalIgnoreCase);
            });
    }

    [Fact]
    public async Task ChangingTheProviderRestatesWhichBinaryWillRun()
    {
        // The statements are bound, not read once. A stale one describes a run that
        // is not the one about to happen.
        var subject = Build(installed: [Agent(0), Agent(1)]);
        await subject.Model.DiscoverAsync();

        var restated = 0;
        subject.Model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AgentAuthoringViewModel.ProviderStatement))
            {
                restated++;
            }
        };

        subject.Model.SelectedProvider = subject.Model.Providers[1];

        Assert.Equal(1, restated);
        Assert.Contains(
            AgentProvider.Known[1].DisplayName, subject.Model.ProviderStatement, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------- scoping

    [Fact]
    public void ScopingToAnEpicNamesTheEpicAndItsIssues()
    {
        var subject = Build();
        var epic = subject.Workspace.Items.First(i => i.Level == BacklogLevel.Epic);

        subject.Model.ScopeTo(epic);

        Assert.Equal(AgentScope.Epic, subject.Model.Scope);
        Assert.Contains(epic.Title, subject.Model.ScopeStatement, StringComparison.Ordinal);
        Assert.Contains("Issues under it", subject.Model.ScopeStatement, StringComparison.Ordinal);
    }

    [Fact]
    public void ScopingToAnIssueNamesItByItsCode()
    {
        var subject = Build();
        var issue = subject.Workspace.Items.First(i => i.Level == BacklogLevel.Issue);

        subject.Model.ScopeTo(issue);

        Assert.Equal(AgentScope.Issue, subject.Model.Scope);
        Assert.Equal("Scoped to the Issue PROJ-101.", subject.Model.ScopeStatement);
    }

    [Fact]
    public void ClearingTheSelectionWidensTheScopeToTheWholeBacklog()
    {
        var subject = Build();
        subject.Model.ScopeTo(subject.Workspace.Items[0]);

        subject.Model.ScopeTo(null);

        Assert.Equal(AgentScope.Backlog, subject.Model.Scope);
        Assert.Null(subject.Model.ScopeLabel);
        Assert.Equal("Scoped to the whole backlog.", subject.Model.ScopeStatement);
    }

    [Fact]
    public void ChoosingTheBacklogScopeDropsTheLabelTheItemLeftBehind()
    {
        // Otherwise the statement says "the whole backlog" while the request still
        // carries an Issue code, and the two disagree about what just ran.
        var subject = Build();
        subject.Model.ScopeTo(subject.Workspace.Items.First(i => i.Level == BacklogLevel.Issue));

        subject.Model.Choose(subject.Model.Scopes[0]);

        Assert.Null(subject.Model.ScopeLabel);
        Assert.Equal("Scoped to the whole backlog.", subject.Model.ScopeStatement);
    }

    // ----------------------------------------------------------- running

    [Fact]
    public async Task RunningIsRefusedUntilThereIsAProfileAProviderAndAPrompt()
    {
        var subject = Build();
        Assert.False(subject.Model.CanRun);

        await subject.Model.DiscoverAsync();
        Assert.False(subject.Model.CanRun);

        subject.Model.Prompt = "   ";
        Assert.False(subject.Model.CanRun);

        subject.Model.Prompt = "Add a task.";
        Assert.True(subject.Model.CanRun);
    }

    [Fact]
    public async Task ARunWithNothingChosenDoesNotReachTheAgent()
    {
        var subject = Build();

        await subject.Model.RunAsync();

        Assert.Empty(subject.History.Records);
        Assert.Equal(Original, subject.Files.Text(subject.Workspace.BacklogPath));
    }

    [Fact]
    public async Task AnEditIsOfferedAsADiffAndTheAgentsOutputIsKept()
    {
        var subject = await ReadyAsync();

        await subject.Model.RunAsync();

        Assert.True(subject.Model.HasReview);
        Assert.NotEmpty(subject.Model.DiffLines);
        Assert.Equal("working…", Assert.Single(await OutputAsync(subject.Model)));
        Assert.Equal("+1 −0", subject.Model.DiffSummary);
        Assert.False(subject.Model.IsRunning);
    }

    [Fact]
    public async Task TheRequestCarriesThePromptAndTheScopeTheSurfaceWasShowing()
    {
        var subject = await ReadyAsync();
        subject.Model.ScopeTo(subject.Workspace.Items.First(i => i.Level == BacklogLevel.Issue));

        await subject.Model.RunAsync();

        var record = Assert.Single(subject.History.Records);
        Assert.Equal("Add a second task to PROJ-101.", record.Prompt);
        Assert.Equal(nameof(AgentScope.Issue), record.Scope);
        Assert.Equal("PROJ-101", record.ScopeLabel);
    }

    [Fact]
    public async Task WhileAnEditIsUnderReviewASecondRunIsRefused()
    {
        // Running again would overwrite the file the reviewer is deciding about,
        // and the diff on screen would then describe neither state.
        var subject = await ReadyAsync();
        await subject.Model.RunAsync();

        Assert.True(subject.Model.HasReview);
        Assert.False(subject.Model.CanRun);

        await subject.Model.RunAsync();

        Assert.Single(subject.History.Records);
    }

    [Fact]
    public async Task ARunThatChangedNothingIsSaidToHaveChangedNothing()
    {
        var subject = await ReadyAsync(agentWrites: Original);

        await subject.Model.RunAsync();

        Assert.False(subject.Model.HasReview);
        Assert.Empty(subject.Model.DiffLines);
        Assert.Contains("no change", subject.Model.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ARunThatFailedRestoresTheFileAndOffersNoDiff()
    {
        var subject = await ReadyAsync(status: AgentRunStatus.Failed);

        await subject.Model.RunAsync();

        Assert.False(subject.Model.HasReview);
        Assert.Equal(Original, subject.Files.Text(subject.Workspace.BacklogPath));
    }

    [Fact]
    public async Task AnUnparseableEditIsRefusedWithItsReasonRatherThanShown()
    {
        // A diff of a backlog that no longer parses is a diff of something this app
        // cannot plan from, and accepting it would break the profile.
        var subject = await ReadyAsync(agentWrites: "not a backlog at all\n");

        await subject.Model.RunAsync();

        Assert.False(subject.Model.HasReview);
        Assert.True(subject.Model.HasError);
        Assert.Equal(Original, subject.Files.Text(subject.Workspace.BacklogPath));
    }

    // ---------------------------------------------------------- the verdict

    [Fact]
    public async Task AcceptingKeepsTheEditRecordsTheVerdictAndHandsOverTheParse()
    {
        var subject = await ReadyAsync();
        await subject.Model.RunAsync();

        AgentEditReview? handed = null;
        subject.Model.EditAccepted = review => handed = review;

        await subject.Model.AcceptAsync();

        Assert.Equal(Edited, subject.Files.Text(subject.Workspace.BacklogPath));
        Assert.Equal([(1L, true)], subject.History.Verdicts);
        Assert.NotNull(handed);
        Assert.False(subject.Model.HasReview);
        Assert.Empty(subject.Model.DiffLines);
    }

    [Fact]
    public async Task RejectingPutsTheFileBackAndRecordsTheVerdict()
    {
        var subject = await ReadyAsync();
        await subject.Model.RunAsync();

        await subject.Model.RejectAsync();

        Assert.Equal(Original, subject.Files.Text(subject.Workspace.BacklogPath));
        Assert.Equal([(1L, false)], subject.History.Verdicts);
        Assert.False(subject.Model.HasReview);
        Assert.Contains("exactly as it was", subject.Model.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedRestoreIsReportedRatherThanClaimingTheBacklogIsBack()
    {
        var subject = await ReadyAsync();
        await subject.Model.RunAsync();
        subject.Files.WriteError = Error.SourceFailure("agent.edit.unwritable", "read-only volume");

        await subject.Model.RejectAsync();

        Assert.True(subject.Model.HasError);
        Assert.Contains("agent.edit.unwritable", subject.Model.ErrorText);
        Assert.Contains("could not be put back", subject.Model.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AVerdictWithNothingUnderReviewDoesNothing()
    {
        var subject = await ReadyAsync();

        await subject.Model.AcceptAsync();
        await subject.Model.RejectAsync();

        Assert.Empty(subject.History.Verdicts);
        Assert.Equal(Original, subject.Files.Text(subject.Workspace.BacklogPath));
    }

    // ------------------------------------------------------ the Plan handoff

    [Fact]
    public async Task PlanningIsOfferedOnlyAfterAnEditWasAccepted()
    {
        var subject = await ReadyAsync();
        Assert.False(subject.Model.CanPlan);

        await subject.Model.RunAsync();
        Assert.False(subject.Model.CanPlan);

        await subject.Model.AcceptAsync();
        Assert.True(subject.Model.CanPlan);
    }

    [Fact]
    public async Task AskingForAPlanOnlyAsksTheShellToOpenIt()
    {
        // ABSD-705. The request carries no plan, no approval and no board write —
        // an agent's involvement removes no step from the Plan/Apply gate.
        var subject = await ReadyAsync();
        await subject.Model.RunAsync();
        await subject.Model.AcceptAsync();

        var asked = 0;
        subject.Model.PlanRequested = () => asked++;

        subject.Model.RequestPlan();

        Assert.Equal(1, asked);
    }

    [Fact]
    public async Task AskingForAPlanBeforeAnAcceptAsksForNothing()
    {
        var subject = await ReadyAsync();
        await subject.Model.RunAsync();

        var asked = 0;
        subject.Model.PlanRequested = () => asked++;

        subject.Model.RequestPlan();

        Assert.Equal(0, asked);
    }

    [Fact]
    public async Task StartingASecondRunClearsTheFirstRunsOutputAndVerdict()
    {
        // A second run that inherited the first one's output would be read as its
        // own, and the accepted-edit flag would still be offering a stale Plan.
        var subject = await ReadyAsync();
        await subject.Model.RunAsync();
        await subject.Model.AcceptAsync();
        Assert.True(subject.Model.CanPlan);

        subject.Model.Prompt = "Do something else.";
        await subject.Model.RunAsync();

        Assert.Equal("working…", Assert.Single(await OutputAsync(subject.Model)));
        Assert.False(subject.Model.HasAcceptedEdit);
    }
}
