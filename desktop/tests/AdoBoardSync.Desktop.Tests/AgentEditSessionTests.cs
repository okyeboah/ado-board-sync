using System.Text;
using AdoBoardSync.Core.Agents;
using AdoBoardSync.Core.Backlog;
using AdoBoardSync.Core.Configuration;
using AdoBoardSync.Core.Results;
using AdoBoardSync.Desktop.Services;

namespace AdoBoardSync.Desktop.Tests;

/// <summary>
/// Pins ABSD-704 and ABSD-706: an agent's edit is reviewed as a diff, accepted or
/// rejected whole, and every run is recorded whatever the verdict.
///
/// The two assertions worth the most here are that rejecting restores the file
/// <em>byte for byte</em> — not "re-writes equivalent text" — and that a run the
/// user threw away is still in the history, because an agent whose edits nobody
/// keeps is exactly the pattern the record exists to make visible.
/// </summary>
public class AgentEditSessionTests
{
    private const string Original = """
        ## Epic 1: Foundation

        Epic body.

        ### PROJ-101 · First issue

        First body.

        - Do the first thing
        """;

    private static BoardConfig Config() =>
        BoardConfig.Parse(
            """{"org":"o","project":"p","code_prefix":"PROJ","board_file":"backlog.md"}""",
            Path.GetTempPath()).Value;

    private static BacklogWorkspace Workspace(string markdown = Original)
    {
        var config = Config();
        return new BacklogWorkspace(
            null, config, Path.Combine(Path.GetTempPath(), "backlog.md"), markdown,
            BacklogParser.Parse(config, markdown), 0,
            FileStamp.For(DateTimeOffset.UnixEpoch, markdown));
    }

    private static InstalledAgent Agent() =>
        new(AgentProvider.Known[0], "/usr/local/bin/claude", "1.2.3");

    /// <summary>A backlog file that lives in a dictionary, so a test can watch the
    /// exact bytes rather than trust a filesystem round trip.</summary>
    private sealed class FakeFileStore : IAgentEditFileStore
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

        public List<string> Writes { get; } = [];

        public Error? ReadError { get; set; }

        public Error? WriteError { get; set; }

        public FakeFileStore Seed(string path, string text)
        {
            _files[path] = Encoding.UTF8.GetBytes(text);
            return this;
        }

        public byte[] Bytes(string path) => _files[path];

        public string Text(string path) => Encoding.UTF8.GetString(_files[path]);

        public Result<byte[]> ReadBytes(string path) =>
            ReadError is { } error ? error
            : _files.TryGetValue(path, out var bytes) ? bytes
            : Error.NotFound("agent.edit.not_found", $"File not found: {path}.");

        public Result<bool> WriteBytes(string path, byte[] bytes)
        {
            if (WriteError is { } error)
            {
                return error;
            }

            Writes.Add(path);
            _files[path] = bytes;
            return true;
        }
    }

    /// <summary>An agent that writes whatever the test told it to, and reports how it ended.</summary>
    private sealed class FakeRunner(
        FakeFileStore files, string path, string? writes, AgentRunStatus status = AgentRunStatus.Succeeded)
        : IAgentRunner
    {
        public Error? Failure { get; set; }

        public List<AgentRunRequest> Requests { get; } = [];

        public Task<Result<AgentRunResult>> RunAsync(
            AgentRunRequest request, IProgress<string>? output = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);

            if (Failure is { } failure)
            {
                return Task.FromResult<Result<AgentRunResult>>(failure);
            }

            // A real agent edits the file even when it then fails or is cancelled;
            // that is precisely the half-written state the session must undo.
            if (writes is not null)
            {
                files.WriteBytes(path, Encoding.UTF8.GetBytes(writes));
                files.Writes.Clear();
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

        public Error? RecordError { get; set; }

        public Task<Result<long>> RecordRunAsync(
            AgentRunRecord record, CancellationToken cancellationToken = default)
        {
            if (RecordError is { } error)
            {
                return Task.FromResult<Result<long>>(error);
            }

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

    private static AgentEditRequest Request(BacklogWorkspace workspace, string prompt = "Add a task.") =>
        new()
        {
            Workspace = workspace,
            Agent = Agent(),
            Prompt = prompt,
            Scope = AgentScope.Backlog,
        };

    private static (AgentEditSession Session, FakeFileStore Files, FakeAgentHistory History) Subject(
        string? agentWrites, AgentRunStatus status = AgentRunStatus.Succeeded, string original = Original)
    {
        var workspace = Workspace(original);
        var files = new FakeFileStore().Seed(workspace.BacklogPath, original);
        var history = new FakeAgentHistory();
        var runner = new FakeRunner(files, workspace.BacklogPath, agentWrites, status);

        return (new AgentEditSession(runner, files, history), files, history);
    }

    private const string Edited = """
        ## Epic 1: Foundation

        Epic body.

        ### PROJ-101 · First issue

        First body.

        - Do the first thing
        - Do the second thing
        """;

    [Fact]
    public async Task AnEditIsHeldForReviewRatherThanTakenSilently()
    {
        var (session, _, _) = Subject(Edited);

        var proposal = await session.RunAsync(Request(Workspace()));

        Assert.True(proposal.IsSuccess);
        Assert.Equal(AgentEditOutcome.UnderReview, proposal.Value.Outcome);
        Assert.True(proposal.Value.IsUnderReview);
        Assert.True(proposal.Value.Review!.Diff.HasChanges);
    }

    [Fact]
    public async Task RejectingPutsTheFileBackByteForByte()
    {
        // Byte-for-byte, not "equivalent text": a restore that re-serialised the
        // parse would quietly normalise line endings and the trailing newline.
        var workspace = Workspace();
        var files = new FakeFileStore().Seed(workspace.BacklogPath, Original);
        var before = files.Bytes(workspace.BacklogPath).ToArray();

        var session = new AgentEditSession(
            new FakeRunner(files, workspace.BacklogPath, Edited), files, new FakeAgentHistory());

        var proposal = await session.RunAsync(Request(workspace));
        Assert.NotEqual(before, files.Bytes(workspace.BacklogPath));

        var rejected = await session.RejectAsync(proposal.Value);

        Assert.True(rejected.IsSuccess);
        Assert.Equal(before, files.Bytes(workspace.BacklogPath));
    }

    [Fact]
    public async Task AcceptingKeepsWhatTheAgentWroteAndHandsBackTheReparsedItems()
    {
        var (session, files, _) = Subject(Edited);
        var workspace = Workspace();

        var proposal = await session.RunAsync(Request(workspace));
        var accepted = await session.AcceptAsync(proposal.Value);

        Assert.True(accepted.IsSuccess);
        Assert.Equal(Edited, files.Text(workspace.BacklogPath));

        // Re-parsed before the editor is shown it, so an accepted edit cannot hand
        // the editor a backlog it could not read.
        var issue = Assert.Single(proposal.Value.Review!.Items, i => i.Code == "PROJ-101");
        Assert.Equal(["Do the first thing", "Do the second thing"], issue.Bullets);
    }

    [Fact]
    public async Task AnEditThatWouldNotParseIsRefusedAndTheFilePutBack()
    {
        // The heading structure is what the parser reads the backlog by. An agent
        // that removes every Epic heading has produced a file the app cannot open,
        // and showing that as a reviewable diff would offer to accept it.
        var workspace = Workspace();
        var files = new FakeFileStore().Seed(workspace.BacklogPath, Original);
        var session = new AgentEditSession(
            new FakeRunner(files, workspace.BacklogPath, "no headings here at all\n"),
            files, new FakeAgentHistory());

        var proposal = await session.RunAsync(Request(workspace));

        Assert.Equal(AgentEditOutcome.Refused, proposal.Value.Outcome);
        Assert.NotNull(proposal.Value.Refusal);
        Assert.Equal(Original, files.Text(workspace.BacklogPath));
    }

    [Fact]
    public async Task AnAgentThatChangedNothingIsSaidSoRatherThanShownAnEmptyDiff()
    {
        var (session, _, _) = Subject(agentWrites: null);

        var proposal = await session.RunAsync(Request(Workspace()));

        Assert.Equal(AgentEditOutcome.NoChange, proposal.Value.Outcome);
        Assert.False(proposal.Value.IsUnderReview);
    }

    [Theory]
    [InlineData(AgentRunStatus.Cancelled)]
    [InlineData(AgentRunStatus.TimedOut)]
    [InlineData(AgentRunStatus.Failed)]
    public async Task ARunThatDidNotFinishLeavesTheFileAsItWas(AgentRunStatus status)
    {
        // A half-written backlog is not a proposal: the agent was interrupted
        // mid-edit, and the diff would describe an accident.
        var workspace = Workspace();
        var files = new FakeFileStore().Seed(workspace.BacklogPath, Original);
        var session = new AgentEditSession(
            new FakeRunner(files, workspace.BacklogPath, Edited, status), files, new FakeAgentHistory());

        var proposal = await session.RunAsync(Request(workspace));

        Assert.Equal(AgentEditOutcome.NoRun, proposal.Value.Outcome);
        Assert.Equal(Original, files.Text(workspace.BacklogPath));
    }

    [Fact]
    public async Task TheProviderAndTheScopeAreRecordedWithEveryRun()
    {
        var (session, _, history) = Subject(Edited);

        await session.RunAsync(Request(Workspace(), "Add the second task."));

        var record = Assert.Single(history.Records);
        Assert.Equal("claude", record.ProviderId);
        Assert.Equal("1.2.3", record.ProviderVersion);
        Assert.Equal("Add the second task.", record.Prompt);
        Assert.Equal("Backlog", record.Scope);
        Assert.Equal("o/p", record.ProfileKey);

        // Still open: an edit under review has not finished from the user's point
        // of view, whatever the process did.
        Assert.Null(record.FinishedAt);
        Assert.Null(record.EditAccepted);
    }

    [Fact]
    public async Task ARejectedEditIsStillRecordedWithItsVerdict()
    {
        var (session, _, history) = Subject(Edited);

        var proposal = await session.RunAsync(Request(Workspace()));
        await session.RejectAsync(proposal.Value);

        Assert.Single(history.Records);
        Assert.Equal((1L, false), Assert.Single(history.Verdicts));
    }

    [Fact]
    public async Task AnAcceptedEditIsRecordedWithItsVerdict()
    {
        var (session, _, history) = Subject(Edited);

        var proposal = await session.RunAsync(Request(Workspace()));
        await session.AcceptAsync(proposal.Value);

        Assert.Equal((1L, true), Assert.Single(history.Verdicts));
    }

    [Fact]
    public async Task ARunThatNeverStartedIsRecordedToo()
    {
        // "The CLI would not start" is attributable history: it is how a user finds
        // out their agent broke rather than their backlog.
        var workspace = Workspace();
        var files = new FakeFileStore().Seed(workspace.BacklogPath, Original);
        var history = new FakeAgentHistory();
        var runner = new FakeRunner(files, workspace.BacklogPath, null)
        {
            Failure = Error.NotFound("agent.not_found", "claude is no longer on PATH."),
        };

        var proposal = await new AgentEditSession(runner, files, history).RunAsync(Request(workspace));

        Assert.Equal(AgentEditOutcome.NoRun, proposal.Value.Outcome);
        Assert.Equal(false, Assert.Single(history.Records).EditAccepted);
    }

    [Fact]
    public async Task AVerdictOnARunTheHistoryRefusedIsReportedRatherThanLost()
    {
        var workspace = Workspace();
        var files = new FakeFileStore().Seed(workspace.BacklogPath, Original);
        var history = new FakeAgentHistory
        {
            RecordError = Error.SourceFailure("history.unwritable", "The database is read-only."),
        };

        var session = new AgentEditSession(
            new FakeRunner(files, workspace.BacklogPath, Edited), files, history);

        var proposal = await session.RunAsync(Request(workspace));

        // The diff is still reviewable — the user's work is not held hostage to the
        // audit trail — but the verdict has nothing to attach to and says so.
        Assert.True(proposal.Value.IsUnderReview);
        Assert.NotNull(proposal.Value.HistoryError);

        var accepted = await session.AcceptAsync(proposal.Value);
        Assert.Equal("agent.edit.unrecorded", accepted.Error!.Code);
    }

    [Fact]
    public async Task AFailedRestoreIsReportedRatherThanSwallowed()
    {
        var workspace = Workspace();
        var files = new FakeFileStore().Seed(workspace.BacklogPath, Original);
        var session = new AgentEditSession(
            new FakeRunner(files, workspace.BacklogPath, Edited), files, new FakeAgentHistory());

        var proposal = await session.RunAsync(Request(workspace));
        files.WriteError = Error.SourceFailure("agent.edit.unwritable", "The file is read-only.");

        var rejected = await session.RejectAsync(proposal.Value);

        Assert.True(rejected.IsFailure);
        Assert.Equal("agent.edit.unwritable", rejected.Error!.Code);
    }

    [Fact]
    public void TheComposedPromptStatesTheScopeAndTheOneFileTheAgentMayChange()
    {
        // The surface tells the user which provider runs, what it can read and what
        // it may change (ABSD-703). Telling the agent something else would make that
        // disclosure a lie.
        var workspace = Workspace();
        var prompt = AgentEditSession.ComposePrompt(new AgentEditRequest
        {
            Workspace = workspace,
            Agent = Agent(),
            Prompt = "Split PROJ-101.",
            Scope = AgentScope.Issue,
            ScopeLabel = "PROJ-101",
        });

        Assert.Contains(workspace.BacklogPath, prompt, StringComparison.Ordinal);
        Assert.Contains("the Issue PROJ-101", prompt, StringComparison.Ordinal);
        Assert.Contains("Do not touch Azure DevOps", prompt, StringComparison.Ordinal);
        Assert.Contains("Split PROJ-101.", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AcceptingSomethingThatIsNotUnderReviewIsRefused()
    {
        var (session, _, _) = Subject(agentWrites: null);

        var proposal = await session.RunAsync(Request(Workspace()));

        Assert.Equal("agent.edit.nothing_to_accept", (await session.AcceptAsync(proposal.Value)).Error!.Code);
        Assert.Equal("agent.edit.nothing_to_reject", (await session.RejectAsync(proposal.Value)).Error!.Code);
    }
}
