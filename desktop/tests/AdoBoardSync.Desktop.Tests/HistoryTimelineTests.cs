using AdoBoardSync.Core.Backlog;
using AdoBoardSync.Core.Configuration;
using AdoBoardSync.Core.Operations;
using AdoBoardSync.Core.Planning;
using AdoBoardSync.Core.Results;
using AdoBoardSync.Desktop.Services;
using AdoBoardSync.Desktop.ViewModels;

namespace AdoBoardSync.Desktop.Tests;

/// <summary>
/// Pins the Apply recorder (ABSD-501's integration) and the History timeline
/// (ABSD-508).
///
/// Two rules matter more than the rest. Recording must never be able to fail the
/// Apply it is recording — a broken history is a support problem, an aborted
/// write is a correctness one. And the timeline must never show one profile's
/// runs while another profile is open.
/// </summary>
public class HistoryTimelineTests
{
    private const string Markdown = "## Epic 1\n";

    private static BoardConfig Config(string org = "o", string project = "p") =>
        BoardConfig.Parse(
            $$"""{"org":"{{org}}","project":"{{project}}","code_prefix":"PROJ","board_file":"backlog.md"}""",
            Path.GetTempPath()).Value;

    private static BacklogWorkspace Workspace(BoardConfig config) =>
        new(null, config, "backlog.md", Markdown, [], 0,
            FileStamp.For(DateTimeOffset.UnixEpoch, Markdown));

    private static ApplyOutcome Outcome(bool succeeded, string title = "PROJ-101 · A") =>
        new(new PlanRow
        {
            Operation = PlanOperation.Create,
            Level = BacklogLevel.Issue,
            Title = title,
            Code = "PROJ-101",
        }, succeeded, succeeded ? 42 : null, succeeded ? "Created #42" : "That token was rejected.");

    /// <summary>An in-memory history. Fails on demand, so the recorder's promise
    /// that a broken store cannot break an Apply is testable.</summary>
    private sealed class FakeHistory : IOperationHistory
    {
        private long _nextId = 1;

        public List<OperationRun> Runs { get; } = [];

        public List<OperationItemOutcome> Outcomes { get; } = [];

        public Error? BeginError { get; set; }

        public Error? RecordError { get; set; }

        public Error? CompleteError { get; set; }

        public Error? ListError { get; set; }

        /// <summary>Makes a write take long enough that closing the run could race it.</summary>
        public TimeSpan WriteDelay { get; set; }

        private readonly Lock _gate = new();

        public Task<Result<long>> BeginRunAsync(
            string profileKey, string command, DateTimeOffset startedAt, CancellationToken cancellationToken = default)
        {
            if (BeginError is { } error)
            {
                return Task.FromResult<Result<long>>(error);
            }

            var id = _nextId++;
            Runs.Add(new OperationRun
            {
                Id = id,
                ProfileKey = profileKey,
                Command = command,
                StartedAt = startedAt,
            });

            return Task.FromResult<Result<long>>(id);
        }

        public async Task<Result<bool>> RecordOutcomeAsync(
            long runId, OperationItemOutcome outcome, CancellationToken cancellationToken = default)
        {
            if (RecordError is { } error)
            {
                return error;
            }

            if (WriteDelay > TimeSpan.Zero)
            {
                await Task.Delay(WriteDelay, cancellationToken);
            }

            // The list is not thread-safe; the recorder serialises its writes, and
            // this lock is what would expose it if it stopped doing so.
            lock (_gate)
            {
                Outcomes.Add(outcome);
            }

            return true;
        }

        public Task<Result<bool>> CompleteRunAsync(
            long runId, DateTimeOffset finishedAt, int succeeded, int failed, string summary,
            CancellationToken cancellationToken = default)
        {
            if (CompleteError is { } error)
            {
                return Task.FromResult<Result<bool>>(error);
            }

            var index = Runs.FindIndex(r => r.Id == runId);
            Runs[index] = Runs[index] with
            {
                FinishedAt = finishedAt,
                Succeeded = succeeded,
                Failed = failed,
                Summary = summary,
            };

            return Task.FromResult<Result<bool>>(true);
        }

        public Task<Result<IReadOnlyList<OperationRun>>> ListRunsAsync(
            string profileKey, int limit, CancellationToken cancellationToken = default)
        {
            if (ListError is { } error)
            {
                return Task.FromResult<Result<IReadOnlyList<OperationRun>>>(error);
            }

            OperationRun[] matching =
            [
                .. Runs.Where(r => r.ProfileKey == profileKey)
                    .OrderByDescending(r => r.StartedAt)
                    .Take(limit)
            ];

            return Task.FromResult<Result<IReadOnlyList<OperationRun>>>(matching);
        }

        public Task<Result<IReadOnlyList<OperationItemOutcome>>> ListOutcomesAsync(
            long runId, CancellationToken cancellationToken = default)
        {
            OperationItemOutcome[] matching =
                [.. Outcomes.Where(o => o.RunId == runId).OrderBy(o => o.Sequence)];

            return Task.FromResult<Result<IReadOnlyList<OperationItemOutcome>>>(matching);
        }
    }

    private static readonly DateTimeOffset Noon =
        new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    // -------------------------------------------------------------- recorder

    [Fact]
    public async Task ARunIsOpenedBeforeTheWritesAndClosedWithItsTotals()
    {
        var history = new FakeHistory();
        var recorder = new ApplyHistoryRecorder(history);

        await recorder.BeginAsync("o/p", PlanCommand.Import, Noon);
        Assert.True(recorder.IsRecording);
        Assert.Null(Assert.Single(history.Runs).FinishedAt);

        await recorder.RecordAsync(Outcome(true), Noon);
        await recorder.RecordAsync(Outcome(false, "PROJ-102 · B"), Noon);
        await recorder.CompleteAsync("Applied 1, failed 1.", Noon.AddSeconds(3));

        var run = Assert.Single(history.Runs);
        Assert.Equal("Import", run.Command);
        Assert.Equal(1, run.Succeeded);
        Assert.Equal(1, run.Failed);
        Assert.Equal(Noon.AddSeconds(3), run.FinishedAt);
        Assert.False(recorder.IsRecording);
    }

    [Fact]
    public async Task OutcomesAreRecordedInTheReviewedPlansOrder()
    {
        var history = new FakeHistory();
        var recorder = new ApplyHistoryRecorder(history);

        await recorder.BeginAsync("o/p", PlanCommand.Import, Noon);
        await recorder.RecordAsync(Outcome(true, "first"), Noon);
        await recorder.RecordAsync(Outcome(true, "second"), Noon);
        await recorder.CompleteAsync("done", Noon);

        Assert.Equal([0, 1], history.Outcomes.Select(o => o.Sequence));
        Assert.Equal(["first", "second"], history.Outcomes.Select(o => o.Title));
    }

    [Fact]
    public async Task AHistoryThatCannotBeOpenedDoesNotStopTheApplyFromBeingRecordedAsCounted()
    {
        // The promise: a store failure never propagates. The Apply carries on and
        // the recorder simply has nothing to write to.
        var history = new FakeHistory
        {
            BeginError = Error.SourceFailure("history.unwritable", "The database is read-only."),
        };

        var recorder = new ApplyHistoryRecorder(history);

        await recorder.BeginAsync("o/p", PlanCommand.Import, Noon);
        await recorder.RecordAsync(Outcome(true), Noon);
        await recorder.CompleteAsync("done", Noon);

        Assert.False(recorder.IsRecording);
        Assert.Empty(history.Runs);
        Assert.Empty(history.Outcomes);
    }

    [Fact]
    public async Task AFailureRecordingOneOutcomeDoesNotThrowEither()
    {
        var history = new FakeHistory
        {
            RecordError = Error.SourceFailure("history.unwritable", "Disk full."),
        };

        var recorder = new ApplyHistoryRecorder(history);

        await recorder.BeginAsync("o/p", PlanCommand.Import, Noon);
        await recorder.RecordAsync(Outcome(true), Noon);
        await recorder.CompleteAsync("done", Noon);

        // The run still closes with the totals the recorder counted itself, so the
        // summary is right even when the per-row detail was lost.
        Assert.Equal(1, Assert.Single(history.Runs).Succeeded);
    }

    [Fact]
    public async Task OutcomesArrivingConcurrentlyEachGetTheirOwnSequenceAndAreAllCounted()
    {
        // Apply fans its writes out over worker tasks and reports each outcome
        // through IProgress, so two can arrive at once on thread-pool threads.
        // Before the recorder took a lock, the counters raced and two outcomes
        // could claim the same position in the reviewed Plan's order.
        var history = new FakeHistory();
        var recorder = new ApplyHistoryRecorder(history);
        await recorder.BeginAsync("o/p", PlanCommand.ResyncTasks, Noon);

        const int Rows = 64;
        await Task.WhenAll(Enumerable.Range(0, Rows).Select(i =>
            Task.Run(() => recorder.RecordAsync(Outcome(i % 2 == 0, $"row {i}"), Noon))));

        await recorder.CompleteAsync("done", Noon);

        var run = Assert.Single(history.Runs);
        Assert.Equal(Rows / 2, run.Succeeded);
        Assert.Equal(Rows / 2, run.Failed);

        // Every row landed, and each took a distinct position.
        Assert.Equal(Rows, history.Outcomes.Count);
        Assert.Equal(Rows, history.Outcomes.Select(o => o.Sequence).Distinct().Count());
    }

    [Fact]
    public async Task ARunIsNotClosedUntilItsOwnOutcomesHaveLanded()
    {
        // A completed run missing its own rows would be a consistent store and a
        // dishonest record.
        var history = new FakeHistory { WriteDelay = TimeSpan.FromMilliseconds(20) };
        var recorder = new ApplyHistoryRecorder(history);
        await recorder.BeginAsync("o/p", PlanCommand.Import, Noon);

        // Fire-and-forget, exactly as the Plan gate's progress callback does.
        _ = recorder.RecordAsync(Outcome(true, "slow row"), Noon);

        await recorder.CompleteAsync("done", Noon);

        Assert.Single(history.Outcomes);
        Assert.NotNull(Assert.Single(history.Runs).FinishedAt);
    }

    [Fact]
    public async Task AnAbandonedRunStaysOpenBecauseThatIsWhatActuallyHappened()
    {
        // The Plan was refused before any write. Inventing a completion would say
        // the run ended cleanly when nothing ran at all.
        var history = new FakeHistory();
        var recorder = new ApplyHistoryRecorder(history);

        await recorder.BeginAsync("o/p", PlanCommand.Import, Noon);
        recorder.Abandon();
        await recorder.CompleteAsync("never runs", Noon);

        Assert.Null(Assert.Single(history.Runs).FinishedAt);
    }

    // -------------------------------------------------------------- timeline

    [Fact]
    public async Task TheTimelineShowsOnlyTheActiveProfilesRuns()
    {
        var history = new FakeHistory();
        await history.BeginRunAsync("o/p", "Import", Noon);
        await history.BeginRunAsync("other/board", "Resync", Noon.AddMinutes(1));

        var timeline = new HistoryViewModel(history);
        await timeline.LoadAsync(Workspace(Config()));

        Assert.Equal("Import", Assert.Single(timeline.Runs).Command);
    }

    [Fact]
    public async Task SwitchingProfileDoesNotLeaveThePreviousProfilesRunsOnScreen()
    {
        var history = new FakeHistory();
        await history.BeginRunAsync("o/p", "Import", Noon);

        var timeline = new HistoryViewModel(history);
        await timeline.LoadAsync(Workspace(Config()));
        Assert.Single(timeline.Runs);

        await timeline.LoadAsync(Workspace(Config("other", "board")));

        Assert.Empty(timeline.Runs);
        Assert.True(timeline.IsEmpty);
    }

    [Fact]
    public async Task RunsAreNewestFirst()
    {
        var history = new FakeHistory();
        await history.BeginRunAsync("o/p", "Import", Noon);
        await history.BeginRunAsync("o/p", "Resync", Noon.AddHours(1));

        var timeline = new HistoryViewModel(history);
        await timeline.LoadAsync(Workspace(Config()));

        Assert.Equal(["Resync", "Import"], timeline.Runs.Select(r => r.Command));
    }

    [Fact]
    public async Task AnInterruptedRunIsShownAsInterruptedRatherThanHidden()
    {
        // The board may hold half of it, and that is precisely when someone looks.
        var history = new FakeHistory();
        await history.BeginRunAsync("o/p", "Import", Noon);

        var timeline = new HistoryViewModel(history);
        await timeline.LoadAsync(Workspace(Config()));

        var run = Assert.Single(timeline.Runs);
        Assert.True(run.WasInterrupted);
        Assert.Equal("!", run.Glyph);
        Assert.Contains("Interrupted", run.Result);
        Assert.Equal("unfinished", run.Duration);
    }

    [Fact]
    public async Task OutcomesLoadOnlyWhenARunIsExpanded()
    {
        var history = new FakeHistory();
        var recorder = new ApplyHistoryRecorder(history);
        await recorder.BeginAsync("o/p", PlanCommand.Import, Noon);
        await recorder.RecordAsync(Outcome(true), Noon);
        await recorder.CompleteAsync("Applied 1 change.", Noon);

        var timeline = new HistoryViewModel(history);
        await timeline.LoadAsync(Workspace(Config()));

        var run = Assert.Single(timeline.Runs);
        Assert.False(run.HasOutcomes);

        await timeline.ToggleAsync(run);

        Assert.True(run.IsExpanded);
        Assert.Equal("PROJ-101 · A", Assert.Single(run.Outcomes).Title);

        // Collapsing and expanding again must not fetch or duplicate them.
        await timeline.ToggleAsync(run);
        await timeline.ToggleAsync(run);
        Assert.Single(run.Outcomes);
    }

    [Fact]
    public async Task AnUnreadableHistoryIsReportedWithItsTypedCode()
    {
        var history = new FakeHistory
        {
            ListError = Error.SourceFailure("history.unreadable", "The database is locked."),
        };

        var timeline = new HistoryViewModel(history);
        await timeline.LoadAsync(Workspace(Config()));

        Assert.True(timeline.HasError);
        Assert.Contains("history.unreadable", timeline.ErrorText);
    }

    [Fact]
    public async Task AProfileWithNoRunsSaysSoRatherThanLookingBroken()
    {
        var timeline = new HistoryViewModel(new FakeHistory());

        await timeline.LoadAsync(Workspace(Config()));

        Assert.True(timeline.IsEmpty);
        Assert.Contains("No Apply has run", timeline.StatusText);
    }

    [Fact]
    public void ClearingTheTimelineEmptiesItAndForgetsTheProfile()
    {
        var timeline = new HistoryViewModel(new FakeHistory());

        timeline.Clear();

        Assert.Empty(timeline.Runs);
        Assert.False(timeline.IsEmpty);
    }
}
