using AdoBoardSync.Infrastructure;
using AdoBoardSync.Core.Agents;
using AdoBoardSync.Core.Operations;
using AdoBoardSync.Core.Results;
using AdoBoardSync.Infrastructure.Operations;

namespace AdoBoardSync.Desktop.Tests;

/// <summary>
/// The ABSD-501 store against a real file on disk. An in-memory fake would prove
/// what callers do with the port; only SQLite can prove what the adapter promises
/// about it — that a completed run cannot be completed twice, that a crash leaves a
/// run open rather than losing it, that one profile never sees another's runs, and
/// that two writers sharing the file both land every row.
/// </summary>
public class OperationHistoryTests
{
    private const string Contoso = "contoso/board";
    private const string Fabrikam = "fabrikam/board";

    [Fact]
    public async Task ARunRoundTripsWithItsOutcomesInTheReviewedPlansOrder()
    {
        await WithHistoryAsync(async history =>
        {
            var started = Instant("2026-09-03T09:00:00+02:00");
            var runId = Ok(await history.BeginRunAsync(Contoso, "Import", started));

            // Recorded out of order on purpose: Apply runs its waves in parallel, so
            // the order the network answers in is not the order the user approved.
            Ok(await history.RecordOutcomeAsync(runId, Outcome(2, "PROJ-3", "Third", boardId: 103)));
            Ok(await history.RecordOutcomeAsync(runId, Outcome(0, "PROJ-1", "First", boardId: 101)));
            Ok(await history.RecordOutcomeAsync(runId, Outcome(1, null, "Second", succeeded: false)));

            Ok(await history.CompleteRunAsync(
                runId, Instant("2026-09-03T09:00:41+02:00"), succeeded: 2, failed: 1, "Applied 2, failed 1."));

            var run = Assert.Single(Ok(await history.ListRunsAsync(Contoso, 10)));
            Assert.Equal(runId, run.Id);
            Assert.Equal(Contoso, run.ProfileKey);
            Assert.Equal("Import", run.Command);
            Assert.Equal(started, run.StartedAt);
            Assert.Equal(Instant("2026-09-03T09:00:41+02:00"), run.FinishedAt);
            Assert.Equal(2, run.Succeeded);
            Assert.Equal(1, run.Failed);
            Assert.Equal("Applied 2, failed 1.", run.Summary);
            Assert.True(run.IsComplete);
            Assert.False(run.AllSucceeded);

            var outcomes = Ok(await history.ListOutcomesAsync(runId));
            Assert.Equal(["First", "Second", "Third"], outcomes.Select(o => o.Title));
            Assert.Equal([0, 1, 2], outcomes.Select(o => o.Sequence));

            var first = outcomes[0];
            Assert.Equal(runId, first.RunId);
            Assert.Equal("Create", first.Operation);
            Assert.Equal("Epic", first.Level);
            Assert.Equal("PROJ-1", first.Code);
            Assert.Equal(101, first.BoardId);
            Assert.True(first.Succeeded);
            Assert.Equal("Created #101", first.Message);

            // A row the Plan carried no code for stays without one rather than
            // acquiring an empty string that a reader would show as a blank cell.
            Assert.Null(outcomes[1].Code);
            Assert.Null(outcomes[1].BoardId);
            Assert.False(outcomes[1].Succeeded);
        });
    }

    [Fact]
    public async Task RunsReadBackNewestFirstWhateverTimezoneTheyWereRecordedIn()
    {
        await WithHistoryAsync(async history =>
        {
            // Local wall clocks say the opposite of the instants: "00:30 on the 3rd"
            // in Auckland happened before "23:00 on the 2nd" in Honolulu. Anything
            // that stored local text would read these back the wrong way round, which
            // is what a machine that changed timezone would do to its own history.
            var auckland = Instant("2026-09-03T00:30:00+13:00");
            var honolulu = Instant("2026-09-02T23:00:00-11:00");
            Assert.True(auckland < honolulu);

            var older = Ok(await history.BeginRunAsync(Contoso, "Import", auckland));
            var newer = Ok(await history.BeginRunAsync(Contoso, "ResyncTasks", honolulu));

            var runs = Ok(await history.ListRunsAsync(Contoso, 10));

            Assert.Equal([newer, older], runs.Select(r => r.Id));
            Assert.Equal(honolulu, runs[0].StartedAt);
            Assert.Equal(auckland, runs[1].StartedAt);
        });
    }

    [Fact]
    public async Task OneProfileNeverSeesAnotherProfilesRuns()
    {
        await WithHistoryAsync(async history =>
        {
            var mine = Ok(await history.BeginRunAsync(Contoso, "Import", Instant("2026-09-03T09:00:00Z")));
            var theirs = Ok(await history.BeginRunAsync(Fabrikam, "Import", Instant("2026-09-03T10:00:00Z")));

            Assert.Equal([mine], Ok(await history.ListRunsAsync(Contoso, 10)).Select(r => r.Id));
            Assert.Equal([theirs], Ok(await history.ListRunsAsync(Fabrikam, 10)).Select(r => r.Id));
            Assert.Empty(Ok(await history.ListRunsAsync("northwind/board", 10)));
        });
    }

    [Fact]
    public async Task ADoubleCompleteIsRefused()
    {
        await WithHistoryAsync(async history =>
        {
            var runId = Ok(await history.BeginRunAsync(Contoso, "Import", Instant("2026-09-03T09:00:00Z")));
            Ok(await history.CompleteRunAsync(
                runId, Instant("2026-09-03T09:00:30Z"), succeeded: 3, failed: 0, "Applied 3 changes."));

            var again = await history.CompleteRunAsync(
                runId, Instant("2026-09-03T11:00:00Z"), succeeded: 99, failed: 99, "Rewritten.");

            Assert.True(again.IsFailure);
            Assert.Equal("history.run_already_complete", again.Error!.Code);
            Assert.Equal(ErrorKind.Conflict, again.Error.Kind);

            // Refused, not merely reported: the totals the first completion wrote are
            // still the ones the timeline reads.
            var run = Assert.Single(Ok(await history.ListRunsAsync(Contoso, 10)));
            Assert.Equal(3, run.Succeeded);
            Assert.Equal(0, run.Failed);
            Assert.Equal("Applied 3 changes.", run.Summary);
            Assert.Equal(Instant("2026-09-03T09:00:30Z"), run.FinishedAt);
        });
    }

    [Fact]
    public async Task AnOutcomeCannotJoinAClosedRunAndAnUnknownRunIsNotFound()
    {
        await WithHistoryAsync(async history =>
        {
            var runId = Ok(await history.BeginRunAsync(Contoso, "Import", Instant("2026-09-03T09:00:00Z")));
            Ok(await history.CompleteRunAsync(
                runId, Instant("2026-09-03T09:00:30Z"), succeeded: 0, failed: 0, "Nothing to apply."));

            var late = await history.RecordOutcomeAsync(runId, Outcome(0, "PROJ-1", "Too late"));
            Assert.True(late.IsFailure);
            Assert.Equal("history.run_already_complete", late.Error!.Code);
            Assert.Equal(ErrorKind.Conflict, late.Error.Kind);
            Assert.Empty(Ok(await history.ListOutcomesAsync(runId)));

            var stray = await history.RecordOutcomeAsync(9_999, Outcome(0, "PROJ-1", "Orphan"));
            Assert.True(stray.IsFailure);
            Assert.Equal("history.run_not_found", stray.Error!.Code);
            Assert.Equal(ErrorKind.NotFound, stray.Error.Kind);
        });
    }

    [Fact]
    public async Task AnInterruptedRunReadsBackUnfinishedRatherThanVanishing()
    {
        await WithHistoryFileAsync(async path =>
        {
            long runId;

            // The store is disposed with the run still open and never completed —
            // what the file looks like after a crash mid-Apply.
            await using (var crashed = new SqliteOperationHistory(path))
            {
                runId = Ok(await crashed.BeginRunAsync(Contoso, "Import", Instant("2026-09-03T09:00:00Z")));
                Ok(await crashed.RecordOutcomeAsync(runId, Outcome(0, "PROJ-1", "Created before the crash", boardId: 101)));
            }

            await using var reopened = new SqliteOperationHistory(path);

            var run = Assert.Single(Ok(await reopened.ListRunsAsync(Contoso, 10)));
            Assert.Equal(runId, run.Id);
            Assert.Null(run.FinishedAt);
            Assert.False(run.IsComplete);
            Assert.False(run.AllSucceeded);
            Assert.Equal(0, run.Total);

            // The work it did get through is still there, which is the point of
            // recording outcomes as they happen rather than in one write at the end.
            var outcome = Assert.Single(Ok(await reopened.ListOutcomesAsync(runId)));
            Assert.Equal("Created before the crash", outcome.Title);

            // And it is still open, so it can be closed by whoever finds it.
            Ok(await reopened.CompleteRunAsync(
                runId, Instant("2026-09-03T09:05:00Z"), succeeded: 1, failed: 0, "Recovered."));
        });
    }

    [Fact]
    public async Task AnAgentRunRoundTripsAndItsVerdictIsSetExactlyOnce()
    {
        await WithHistoryAsync(async history =>
        {
            var started = Instant("2026-09-03T09:00:00+02:00");
            var runId = Ok(await history.RecordRunAsync(new AgentRunRecord
            {
                ProfileKey = Contoso,
                ProviderId = "claude",
                ProviderVersion = "2.1.0",
                Prompt = "Split PROJ-1 into two Issues.",
                Scope = nameof(AgentScope.Epic),
                ScopeLabel = "PROJ-1",
                StartedAt = started,
                Status = nameof(AgentRunStatus.Succeeded),
                ExitCode = 0,
                Summary = "Rewrote one Epic.",
            }));

            var pending = Assert.Single(Ok(await history.ListAgentRunsAsync(Contoso, 10)));
            Assert.Equal(runId, pending.Id);
            Assert.Equal("claude", pending.ProviderId);
            Assert.Equal("2.1.0", pending.ProviderVersion);
            Assert.Equal("Split PROJ-1 into two Issues.", pending.Prompt);
            Assert.Equal("Epic", pending.Scope);
            Assert.Equal("PROJ-1", pending.ScopeLabel);
            Assert.Equal(started, pending.StartedAt);
            Assert.Equal("Succeeded", pending.Status);
            Assert.Null(pending.FinishedAt);
            Assert.Null(pending.EditAccepted);

            var reviewed = Instant("2026-09-03T09:04:00+02:00");
            Ok(await history.RecordVerdictAsync(runId, accepted: true, reviewed));

            var again = await history.RecordVerdictAsync(runId, accepted: false, Instant("2026-09-03T10:00:00+02:00"));
            Assert.True(again.IsFailure);
            Assert.Equal("history.verdict_already_recorded", again.Error!.Code);
            Assert.Equal(ErrorKind.Conflict, again.Error.Kind);

            var settled = Assert.Single(Ok(await history.ListAgentRunsAsync(Contoso, 10)));
            Assert.True(settled.EditAccepted);
            Assert.Equal(reviewed, settled.FinishedAt);

            var stray = await history.RecordVerdictAsync(9_999, accepted: true, reviewed);
            Assert.True(stray.IsFailure);
            Assert.Equal("history.agent_run_not_found", stray.Error!.Code);
            Assert.Equal(ErrorKind.NotFound, stray.Error.Kind);

            Assert.Empty(Ok(await history.ListAgentRunsAsync(Fabrikam, 10)));
        });
    }

    [Fact]
    public async Task AgentRunsAndApplyRunsShareTheFileWithoutCollidingOnIds()
    {
        await WithHistoryAsync(async history =>
        {
            var applyId = Ok(await history.BeginRunAsync(Contoso, "Import", Instant("2026-09-03T09:00:00Z")));
            var agentId = Ok(await history.RecordRunAsync(new AgentRunRecord
            {
                ProfileKey = Contoso,
                ProviderId = "codex",
                ProviderVersion = "0.9.0",
                Prompt = "Tidy the backlog.",
                Scope = nameof(AgentScope.Backlog),
                StartedAt = Instant("2026-09-03T08:00:00Z"),
                Status = nameof(AgentRunStatus.TimedOut),
                ExitCode = 124,
            }));

            // Both tables number from 1, so an id only means something alongside the
            // table it came from — a reader must never look one up in the other.
            Assert.Equal(1, applyId);
            Assert.Equal(1, agentId);

            Assert.Single(Ok(await history.ListRunsAsync(Contoso, 10)));
            Assert.Single(Ok(await history.ListAgentRunsAsync(Contoso, 10)));
        });
    }

    [Fact]
    public async Task TwoWritersOnOneFileBothLandEveryRow()
    {
        await WithHistoryFileAsync(async path =>
        {
            await using var left = new SqliteOperationHistory(path);
            await using var right = new SqliteOperationHistory(path);

            var leftRun = Ok(await left.BeginRunAsync(Contoso, "Import", Instant("2026-09-03T09:00:00Z")));
            var rightRun = Ok(await right.BeginRunAsync(Fabrikam, "ResyncTasks", Instant("2026-09-03T09:00:01Z")));

            // Interleaved and overlapping: the two stores hold separate connections,
            // so these genuinely contend for the file's write lock.
            var writes = Enumerable.Range(0, 24).Select(i => i % 2 == 0
                ? left.RecordOutcomeAsync(leftRun, Outcome(i / 2, $"PROJ-{i}", $"Left {i}"))
                : right.RecordOutcomeAsync(rightRun, Outcome(i / 2, $"PROJ-{i}", $"Right {i}")));

            foreach (var write in await Task.WhenAll(writes))
            {
                Ok(write);
            }

            Ok(await left.CompleteRunAsync(leftRun, Instant("2026-09-03T09:01:00Z"), 12, 0, "Applied 12 changes."));
            Ok(await right.CompleteRunAsync(rightRun, Instant("2026-09-03T09:01:00Z"), 12, 0, "Applied 12 changes."));

            // Read through a third store so the assertion is about the file, not
            // about anything either writer happens to be holding in memory.
            await using var reader = new SqliteOperationHistory(path);

            var mine = Assert.Single(Ok(await reader.ListRunsAsync(Contoso, 10)));
            Assert.Equal(12, mine.Succeeded);
            Assert.Equal(12, Ok(await reader.ListOutcomesAsync(leftRun)).Count);
            Assert.All(Ok(await reader.ListOutcomesAsync(leftRun)), o => Assert.StartsWith("Left ", o.Title));

            var theirs = Assert.Single(Ok(await reader.ListRunsAsync(Fabrikam, 10)));
            Assert.Equal(12, theirs.Succeeded);
            Assert.Equal(12, Ok(await reader.ListOutcomesAsync(rightRun)).Count);
            Assert.All(Ok(await reader.ListOutcomesAsync(rightRun)), o => Assert.StartsWith("Right ", o.Title));
        });
    }

    [Fact]
    public async Task ATimelinePageMustAskForAtLeastOneRun()
    {
        await WithHistoryAsync(async history =>
        {
            var runs = await history.ListRunsAsync(Contoso, 0);
            Assert.True(runs.IsFailure);
            Assert.Equal("history.invalid_limit", runs.Error!.Code);
            Assert.Equal(ErrorKind.Validation, runs.Error.Kind);

            var agents = await history.ListAgentRunsAsync(Contoso, -1);
            Assert.True(agents.IsFailure);
            Assert.Equal("history.invalid_limit", agents.Error!.Code);
        });
    }

    [Fact]
    public async Task ARunWithoutAProfileIsRefusedRatherThanFiledNowhere()
    {
        await WithHistoryAsync(async history =>
        {
            var run = await history.BeginRunAsync("   ", "Import", Instant("2026-09-03T09:00:00Z"));

            Assert.True(run.IsFailure);
            Assert.Equal("history.no_profile", run.Error!.Code);
            Assert.Equal(ErrorKind.Validation, run.Error.Kind);
        });
    }

    [Fact]
    public void TheProfileKeyIsTheOrgAndProjectCaseFolded()
    {
        Assert.Equal("contoso/board", ProfileKey.For("Contoso", "Board"));
        Assert.Equal(ProfileKey.For("contoso", "board"), ProfileKey.For("CONTOSO", "BOARD"));
    }

    [Fact]
    public void TheDefaultDatabaseLivesUnderThisMachinesOwnDataDirectory()
    {
        // The property that matters is that it is not in a repository and not beside
        // a backlog: agent prompts are stored in this file. It is checked against
        // LocalDataPaths rather than the operating system's folder directly because
        // the test run itself redirects that root — see TestDataDirectory, which is
        // also what keeps this assertion from being run against the user's own data.
        var path = SqliteOperationHistory.DefaultDatabasePath();

        Assert.Equal("history.db", Path.GetFileName(path));
        Assert.Equal("ado-board-sync", Path.GetFileName(Path.GetDirectoryName(path)));
        Assert.StartsWith(LocalDataPaths.Root, path, StringComparison.Ordinal);
        Assert.True(Path.IsPathRooted(path));
    }

    [Fact]
    public void TheTestRunsDataIsRedirectedAwayFromTheUsersOwn()
    {
        // The guard on the guard. If the module initialiser ever stops running, this
        // fails here rather than in whichever test quietly appends a fixture profile
        // to the developer's registry.
        var real = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.NotEqual(real, LocalDataPaths.Root);
        Assert.Contains("absd-tests-", LocalDataPaths.Root, StringComparison.Ordinal);
    }

    private static DateTimeOffset Instant(string text) =>
        DateTimeOffset.Parse(text, System.Globalization.CultureInfo.InvariantCulture);

    private static OperationItemOutcome Outcome(
        int sequence, string? code, string title, int? boardId = null, bool succeeded = true) => new()
    {
        Sequence = sequence,
        Operation = "Create",
        Level = "Epic",
        Code = code,
        Title = title,
        BoardId = boardId,
        Succeeded = succeeded,
        Message = boardId is null ? "No board id." : $"Created #{boardId}",
    };

    private static T Ok<T>(Result<T> result)
    {
        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        return result.Value;
    }

    private static Task WithHistoryAsync(Func<SqliteOperationHistory, Task> body) =>
        WithHistoryFileAsync(async path =>
        {
            await using var history = new SqliteOperationHistory(path);
            await body(history);
        });

    private static async Task WithHistoryFileAsync(Func<string, Task> body)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ado-board-sync-history-{Guid.NewGuid():N}");
        try
        {
            await body(Path.Combine(directory, "history.db"));
        }
        finally
        {
            // The store owns its connection rather than a pool, so by here every
            // handle is closed and the WAL sidecars are gone with it.
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A leaked temp directory is not worth failing a green test over.
            }
        }
    }
}
