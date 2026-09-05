using AdoBoardSync.Core.Backlog;
using AdoBoardSync.Core.Board;
using AdoBoardSync.Core.Configuration;
using AdoBoardSync.Core.Planning;
using AdoBoardSync.Core.Results;

namespace AdoBoardSync.Desktop.Tests;

/// <summary>
/// Apply's two promises: it does exactly what the reviewed Plan says, and it
/// refuses once that Plan no longer matches the backlog or the board.
/// </summary>
public class ApplyExecutorTests
{
    private const string Backlog = "## Epic 1\n\n### PROJ-101 · A\n";

    private static string BacklogPrint => PlanBuilder.FingerprintBacklog(Backlog);

    private static Plan ImportPlan(params PlanRow[] rows) => new()
    {
        Command = PlanCommand.Import,
        Rows = rows,
        BacklogFingerprint = BacklogPrint,
        BoardFingerprint = "board-v1",
    };

    private static PlanRow CreateEpic(string title) => new()
    {
        Operation = PlanOperation.Create,
        Level = BacklogLevel.Epic,
        Title = title,
        DescriptionHtml = "<p>epic</p>",
    };

    private static PlanRow CreateIssue(string code, string? parentTitle = null, int? parentId = null) => new()
    {
        Operation = PlanOperation.Create,
        Level = BacklogLevel.Issue,
        Title = $"{code} · A",
        Code = code,
        ParentTitle = parentTitle,
        ParentBoardId = parentId,
        DescriptionHtml = "<p>issue</p>",
    };

    [Fact]
    public async Task ApplyCreatesEveryWriteRowAndNothingElse()
    {
        var gateway = new FakeBoardGateway();
        var plan = ImportPlan(
            CreateIssue("PROJ-101"),
            new PlanRow
            {
                Operation = PlanOperation.Unchanged,
                Level = BacklogLevel.Issue,
                Title = "PROJ-102 · already there",
                Code = "PROJ-102",
                BoardId = 5,
            });

        var report = await ApplyExecutor.ApplyAsync(
            gateway, PlanBuilderTests.Config(), plan, BacklogPrint, "board-v1");

        Assert.True(report.IsSuccess);
        Assert.True(report.Value.AllSucceeded);
        Assert.Single(gateway.Created);
        Assert.Equal("PROJ-101 · A", gateway.Created[0].Title);
    }

    [Fact]
    public async Task AnEpicIsCreatedBeforeTheIssueThatHangsOffIt()
    {
        var gateway = new FakeBoardGateway();
        var plan = ImportPlan(CreateEpic("Epic 1"), CreateIssue("PROJ-101", parentTitle: "Epic 1"));

        var report = await ApplyExecutor.ApplyAsync(
            gateway, PlanBuilderTests.Config(), plan, BacklogPrint, "board-v1");

        Assert.True(report.IsSuccess);
        Assert.Equal(2, gateway.Created.Count);

        var epicId = report.Value.Outcomes[0].BoardId;
        Assert.NotNull(epicId);

        // The Issue must be parented to the id the Epic actually got, which only
        // exists once the Epic has been created in this same run.
        Assert.Equal(epicId, gateway.Created[1].ParentId);
    }

    [Fact]
    public async Task ApplyIsRefusedWhenTheBacklogChangedAfterTheReview()
    {
        var gateway = new FakeBoardGateway();
        var plan = ImportPlan(CreateIssue("PROJ-101"));

        var report = await ApplyExecutor.ApplyAsync(
            gateway, PlanBuilderTests.Config(), plan,
            PlanBuilder.FingerprintBacklog(Backlog + "\n### PROJ-102 · added since\n"),
            "board-v1");

        Assert.True(report.IsFailure);
        Assert.Equal(ErrorKind.Conflict, report.Error!.Kind);
        Assert.Equal("plan.stale_backlog", report.Error.Code);
        Assert.Empty(gateway.Created);
    }

    [Fact]
    public async Task ApplyIsRefusedWhenTheBoardChangedAfterTheReview()
    {
        var gateway = new FakeBoardGateway();
        var plan = ImportPlan(CreateIssue("PROJ-101"));

        var report = await ApplyExecutor.ApplyAsync(
            gateway, PlanBuilderTests.Config(), plan, BacklogPrint, "board-v2-somebody-else-wrote");

        Assert.True(report.IsFailure);
        Assert.Equal("plan.stale_board", report.Error!.Code);

        // Nothing at all was written — the guard runs before the first call.
        Assert.Empty(gateway.Created);
        Assert.Empty(gateway.Updated);
    }

    [Fact]
    public async Task AFailedRowIsReportedWithoutStoppingTheRest()
    {
        var gateway = new FakeBoardGateway { UpdateError = Error.Authorization("board.forbidden", "no rights") };

        var plan = new Plan
        {
            Command = PlanCommand.Resync,
            Rows =
            [
                new PlanRow
                {
                    Operation = PlanOperation.Update,
                    Level = BacklogLevel.Issue,
                    Title = "PROJ-101 · A",
                    Code = "PROJ-101",
                    BoardId = 5,
                    Changes = [new PlanFieldChange(BoardFieldChange.TitleField, "old", "new")],
                },
            ],
            BacklogFingerprint = BacklogPrint,
            BoardFingerprint = "board-v1",
        };

        var report = await ApplyExecutor.ApplyAsync(
            gateway, PlanBuilderTests.Config(), plan, BacklogPrint, "board-v1");

        Assert.True(report.IsSuccess);
        Assert.False(report.Value.AllSucceeded);
        Assert.Equal(1, report.Value.Failed);
        Assert.Contains("no rights", report.Value.Outcomes[0].Message);
    }

    [Fact]
    public async Task UpdateSendsExactlyTheFieldsTheRowNamed()
    {
        var gateway = new FakeBoardGateway();
        var plan = new Plan
        {
            Command = PlanCommand.Resync,
            Rows =
            [
                new PlanRow
                {
                    Operation = PlanOperation.Update,
                    Level = BacklogLevel.Issue,
                    Title = "PROJ-101 · New",
                    Code = "PROJ-101",
                    BoardId = 5,
                    Changes =
                    [
                        new PlanFieldChange(BoardFieldChange.TitleField, "PROJ-101 · Old", "PROJ-101 · New"),
                    ],
                },
            ],
            BacklogFingerprint = BacklogPrint,
            BoardFingerprint = "board-v1",
        };

        await ApplyExecutor.ApplyAsync(gateway, PlanBuilderTests.Config(), plan, BacklogPrint, "board-v1");

        var (id, changes) = Assert.Single(gateway.Updated);
        Assert.Equal(5, id);
        var change = Assert.Single(changes);
        Assert.Equal(BoardFieldChange.TitleField, change.Field);
        Assert.Equal("PROJ-101 · New", change.Value);
    }

    [Fact]
    public async Task ProgressIsReportedRowByRow()
    {
        var gateway = new FakeBoardGateway();
        var plan = ImportPlan(CreateIssue("PROJ-101"), CreateIssue("PROJ-102"));

        var seen = new List<ApplyOutcome>();
        await ApplyExecutor.ApplyAsync(
            gateway, PlanBuilderTests.Config(), plan, BacklogPrint, "board-v1",
            new Progress<ApplyOutcome>(seen.Add));

        // Progress<T> marshals through the synchronisation context, so allow the
        // callbacks to drain before asserting.
        await Task.Delay(50);
        Assert.Equal(2, seen.Count);
    }

    // ---------------------------------------------------------------- deletes

    private static Plan DeletePlan(params PlanRow[] extra) => new()
    {
        Command = PlanCommand.ResyncTasks,
        Rows =
        [
            .. extra,
            new PlanRow
            {
                Operation = PlanOperation.Delete,
                Level = BacklogLevel.Issue,
                Title = "a stray Task",
                Code = "PROJ-101",
                BoardId = 8,
            },
        ],
        BacklogFingerprint = BacklogPrint,
        BoardFingerprint = "board-v1",
    };

    [Fact]
    public async Task ADeleteRowDeletesExactlyTheNamedItem()
    {
        var gateway = new FakeBoardGateway();

        var report = await ApplyExecutor.ApplyAsync(
            gateway, PlanBuilderTests.Config(), DeletePlan(), BacklogPrint, "board-v1");

        Assert.True(report.Value.AllSucceeded);
        Assert.Equal(1, report.Value.Succeeded);
        Assert.Equal(new[] { 8 }, gateway.Deleted);
        Assert.Empty(gateway.Created);
        Assert.Contains("Deleted #8", report.Value.Outcomes[0].Message);
    }

    [Fact]
    public async Task ADeleteWithoutABoardIdIsAFailedOutcomeNotAnException()
    {
        var gateway = new FakeBoardGateway();
        var plan = new Plan
        {
            Command = PlanCommand.ResyncTasks,
            Rows =
            [
                new PlanRow { Operation = PlanOperation.Delete, Level = BacklogLevel.Issue, Title = "x", Code = "PROJ-101" },
            ],
            BacklogFingerprint = BacklogPrint,
            BoardFingerprint = "board-v1",
        };

        var report = await ApplyExecutor.ApplyAsync(
            gateway, PlanBuilderTests.Config(), plan, BacklogPrint, "board-v1");

        Assert.False(report.Value.AllSucceeded);
        Assert.Empty(gateway.Deleted);
        Assert.Contains("no board id to delete", report.Value.Outcomes[0].Message);
    }

    [Fact]
    public async Task AFailedDeleteIsReportedWithoutStoppingTheRest()
    {
        var gateway = new FakeBoardGateway { DeleteError = Error.Authorization("board.forbidden", "no rights") };
        var plan = ImportPlan(CreateIssue("PROJ-101"));

        var report = await ApplyExecutor.ApplyAsync(
            gateway, PlanBuilderTests.Config(),
            DeletePlan(new PlanRow
            {
                Operation = PlanOperation.Create,
                Level = BacklogLevel.Issue,
                Title = "PROJ-101 · A",
                Code = "PROJ-101",
                ParentBoardId = 5,
                DescriptionHtml = "<p>t</p>",
            }),
            BacklogPrint, "board-v1");

        Assert.True(report.IsSuccess);          // the run itself completed
        Assert.False(report.Value.AllSucceeded);
        Assert.Single(gateway.Created);         // unaffected row still ran
        var failure = Assert.Single(report.Value.Outcomes, o => !o.Succeeded);
        Assert.Contains("no rights", failure.Message);
    }

    [Fact]
    public async Task TheStaleGuardRefusesTheRunBeforeAnyDelete()
    {
        var gateway = new FakeBoardGateway();

        var report = await ApplyExecutor.ApplyAsync(
            gateway, PlanBuilderTests.Config(), DeletePlan(), BacklogPrint, "board-moved-on");

        Assert.True(report.IsFailure);
        Assert.Empty(gateway.Deleted);
    }

    // ------------------------------------------------------------- concurrency

    private sealed class RecordingGateway : IBoardGateway
    {
        private readonly List<int> _started = [];
        private readonly List<TaskCompletionSource> _startSignals = [];
        private readonly List<TaskCompletionSource> _resumeGates = [];

        public IReadOnlyList<int> StartedOrder => _started;

        /// <summary>N writes. A write publishes a start signal the moment it runs,
        /// then parks on its resume gate so none can finish before all have begun.</summary>
        public RecordingGateway(int writes)
        {
            for (var i = 0; i < writes; i++)
            {
                _startSignals.Add(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                _resumeGates.Add(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            }
        }

        public void Release(int index) => _resumeGates[index].SetResult();

        /// <summary>Completes when write ``index`` has started, not finished.</summary>
        public Task WhenStartedAsync(int index) => _startSignals[index].Task;

        public Task<Result<BoardSnapshot>> ReadAsync(BoardConfig config, CancellationToken ct = default) =>
            Task.FromResult<Result<BoardSnapshot>>(new BoardSnapshot([], "snapshot"));

        public Task<Result<int>> CreateAsync(BoardConfig config, string workItemType, string title,
            string descriptionHtml, int? parentId, CancellationToken ct = default) =>
            ParkedWrite(index => index);

        public Task<Result<bool>> UpdateAsync(BoardConfig config, int workItemId,
            IReadOnlyList<BoardFieldChange> changes, CancellationToken ct = default) =>
            ParkedWrite(_ => true);

        public Task<Result<bool>> DeleteAsync(BoardConfig config, int workItemId, CancellationToken ct = default) =>
            ParkedWrite(_ => true);

        // The sprint-node half of the port is not what this fixture exercises: it
        // exists to observe write ordering, and an iteration node is not a write
        // this test drives.
        public Task<Result<IterationNode>> EnsureIterationAsync(BoardConfig config, string name,
            string? start, string? finish, CancellationToken ct = default) =>
            Task.FromResult<Result<IterationNode>>(new IterationNode(name, $"id-{name}", "created"));

        public Task<Result<string?>> DefaultTeamAsync(BoardConfig config, CancellationToken ct = default) =>
            Task.FromResult<Result<string?>>((string?)null);

        public Task<Result<bool>> AddTeamIterationAsync(BoardConfig config, string team,
            string identifier, CancellationToken ct = default) =>
            Task.FromResult<Result<bool>>(true);

        private async Task<Result<T>> ParkedWrite<T>(Func<int, T> value)
        {
            int index;
            lock (_started)
            {
                index = _started.Count;
                _started.Add(index);
            }

            // Announce the start regardless of who is watching, then park.
            _startSignals[index].TrySetResult();
            await _resumeGates[index].Task;
            return value(index);
        }
    }

    [Fact]
    public async Task ConcurrentWritesStillReportOutcomesInThePlansRowOrder()
    {
        const int count = MaxConcurrencyProbe;
        var recording = new RecordingGateway(count);

        var rows = new PlanRow[count];
        for (var i = 0; i < count; i++)
        {
            rows[i] = new PlanRow
            {
                Operation = PlanOperation.Update,
                Level = BacklogLevel.Issue,
                Title = $"row {i}",
                Code = $"P-{i:00}",
                BoardId = 100 + i,
                Changes = [new PlanFieldChange(BoardFieldChange.TitleField, "a", "b")],
            };
        }

        var plan = new Plan
        {
            Command = PlanCommand.Resync,
            Rows = rows,
            BacklogFingerprint = BacklogPrint,
            BoardFingerprint = "board-v1",
        };

        var run = ApplyExecutor.ApplyAsync(
            recording, PlanBuilderTests.Config(), plan, BacklogPrint, "board-v1");

        // Independent rows all start inside one wave before any is allowed to finish:
        // every write has published its start signal while every resume gate is shut.
        for (var i = 0; i < count; i++)
        {
            await recording.WhenStartedAsync(i);
        }

        // Completion is then forced in exact reverse row order.
        for (var i = count - 1; i >= 0; i--)
        {
            recording.Release(i);
        }

        var report = await run;

        Assert.True(report.Value.AllSucceeded);
        Assert.Equal(count, recording.StartedOrder.Count);
        for (var i = 0; i < count; i++)
        {
            // Completion ran reversed; reported outcomes must not follow it.
            Assert.Equal($"row {i}", report.Value.Outcomes[i].Row.Title);
        }
    }

    private const int MaxConcurrencyProbe = 6;
}
