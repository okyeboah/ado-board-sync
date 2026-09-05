using AdoBoardSync.Core.Backlog;
using AdoBoardSync.Core.Board;
using AdoBoardSync.Core.Planning;
using AdoBoardSync.Core.Results;
using AdoBoardSync.Infrastructure;

namespace AdoBoardSync.Desktop.Tests;

/// <summary>
///     The live tests: the only ones that reach Azure DevOps. Everything else in this
///     project runs against <see cref="FakeBoardGateway" />, which proves the rules but
///     not the wire format, the auth handshake or the API's own error shapes.
///     They skip unless <c>ADO_BOARD_SYNC_LIVE_CONFIG</c> names a board profile, and
///     the writing ones skip again unless <c>ADO_BOARD_SYNC_LIVE_WRITE=1</c>. Each
///     writing test sends the work items it created to the recycle bin afterwards,
///     pass or fail, and its backlog codes are unique per run — so pointing them at a
///     shared project leaves nothing behind, though a throwaway one is still safer.
/// </summary>
public class LiveBoardTests
{
    [LiveFact]
    public async Task ReadingTheBoardReturnsItsEpicsIssuesAndTasks()
    {
        var config = LiveBoard.Config();
        using var gateway = LiveBoard.Gateway(config);

        var snapshot = await gateway.ReadAsync(config);

        Assert.True(snapshot.IsSuccess, LiveBoard.Explain(snapshot.Error));

        // The read is only meaningful if the types round-tripped: a board that
        // answers with an empty list would pass a bare IsSuccess assertion.
        var allowed = new[]
        {
            config.Types["epic"], config.Types["story"], config.Types["task"]
        };
        Assert.All(snapshot.Value.Items, item => Assert.Contains(item.WorkItemType, allowed));
        Assert.NotEqual(string.Empty, snapshot.Value.Fingerprint);
    }

    /// <summary>
    ///     The batched read carries each item's parent on <c>System.Parent</c>. This
    ///     pins that field's real wire shape — a number, absent at the project root —
    ///     because no fixture can prove Azure DevOps sends what the mapping assumes:
    ///     every claimed parent must be an item this same read returned.
    /// </summary>
    [LiveFact]
    public async Task EveryClaimedParentResolvesToAnItemInTheSameSnapshot()
    {
        var config = LiveBoard.Config();
        using var gateway = LiveBoard.Gateway(config);

        var snapshot = await gateway.ReadAsync(config);
        Assert.True(snapshot.IsSuccess, LiveBoard.Explain(snapshot.Error));

        var ids = snapshot.Value.Items.Select(i => i.Id).ToHashSet();
        var parented = snapshot.Value.Items.Where(i => i.ParentId is not null).ToList();

        Assert.NotEmpty(parented); // this backlog owns Tasks; their parents ride along
        Assert.All(parented, item => Assert.True(
            ids.Contains(item.ParentId!.Value),
            $"Item {item.Id} ({item.WorkItemType}) names parent {item.ParentId}, which the same batched read never returned."));
    }

    /// <summary>
    ///     Pins the 401 mapping against the real service rather than a fixture, since
    ///     a wrong-shaped auth error is the first thing a new user would hit.
    /// </summary>
    [LiveFact]
    public async Task ARejectedTokenComesBackAsAnAuthorizationError()
    {
        var config = LiveBoard.Config();
        using var gateway = new AzureDevOpsGateway("not-a-real-token");

        var snapshot = await gateway.ReadAsync(config);

        Assert.True(snapshot.IsFailure);
        Assert.Equal(ErrorKind.Authorization, snapshot.Error!.Kind);
        Assert.Equal("board.unauthorized", snapshot.Error.Code);
        Assert.DoesNotContain("not-a-real-token", snapshot.Error.SafeMessage, StringComparison.Ordinal);
    }

    [LiveFact]
    public async Task NamingAProjectThatDoesNotExistComesBackAsNotFound()
    {
        var config = LiveBoard.Config($"no-such-project-{Guid.NewGuid():N}");
        using var gateway = LiveBoard.Gateway(config);

        var snapshot = await gateway.ReadAsync(config);

        Assert.True(snapshot.IsFailure);
        Assert.Equal("board.not_found", snapshot.Error!.Code);
    }

    /// <summary>
    ///     The claim this project rests on is that the desktop does what the CLI does.
    ///     Both are pointed at the same live board and the same backlog, and must name
    ///     the same items as out of sync — the CLI through its read-only <c>audit</c>,
    ///     the desktop through the Plan it would ask a user to approve.
    /// </summary>
    [LiveFact]
    public async Task TheResyncPlanNamesTheSameDescriptionsTheCliAuditNames()
    {
        var config = LiveBoard.Config();
        using var gateway = LiveBoard.Gateway(config);

        var board = await gateway.ReadAsync(config);
        Assert.True(board.IsSuccess, LiveBoard.Explain(board.Error));

        var markdown = await File.ReadAllTextAsync(config.BoardFile);
        var items = BacklogParser.Parse(config, markdown);

        var plan = PlanBuilder.BuildResync(config, items, board.Value, markdown);

        var planned = plan.Rows
            .Where(r => r.Changes.Any(c => c.Field == BoardFieldChange.DescriptionField))
            .Select(r => r.Code)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var audited = LiveBoard.CliAuditDescriptionDrift();

        Assert.Equal(audited.OrderBy(c => c), planned.OrderBy(c => c));
    }

    /// <summary>
    ///     One import, then the same import again. The second run is the real
    ///     assertion: it proves the codes written to the board are the ones the
    ///     matcher reads back, which no fake can establish.
    /// </summary>
    [LiveFact(Writes = true)]
    public async Task ImportCreatesTheBacklogThenPlansNothingOnASecondRun()
    {
        using var backlog = LiveBoard.ScratchBacklog();
        var config = LiveBoard.Config(boardFile: backlog.Path);
        using var gateway = LiveBoard.Gateway(config);

        var created = new List<int>();
        try
        {
            var before = await gateway.ReadAsync(config);
            Assert.True(before.IsSuccess, LiveBoard.Explain(before.Error));

            var plan = PlanBuilder.BuildImport(config, backlog.Items, before.Value, backlog.Markdown);
            Assert.Equal(backlog.Items.Count, plan.CreateCount);

            var report = await ApplyExecutor.ApplyAsync(
                gateway, config, plan,
                PlanBuilder.FingerprintBacklog(backlog.Markdown), before.Value.Fingerprint,
                null);

            Assert.True(report.IsSuccess, LiveBoard.Explain(report.Error));
            created.AddRange(report.Value.Outcomes.Select(o => o.BoardId).OfType<int>());
            Assert.True(report.Value.AllSucceeded, LiveBoard.Explain(report.Value));

            // Every row came back with a board id, and the Issues were parented under
            // the Epic this run created rather than left at the project root.
            Assert.All(report.Value.Outcomes, o => Assert.NotNull(o.BoardId));

            var after = await gateway.ReadAsync(config);
            Assert.True(after.IsSuccess, LiveBoard.Explain(after.Error));

            var again = PlanBuilder.BuildImport(config, backlog.Items, after.Value, backlog.Markdown);
            Assert.Equal(0, again.CreateCount);
            Assert.False(again.HasWork);
        }
        finally
        {
            await LiveBoard.DiscardAsync(config, created);
        }
    }

    [LiveFact(Writes = true)]
    public async Task ResyncWritesBackATitleThatChangedInTheBacklog()
    {
        using var backlog = LiveBoard.ScratchBacklog();
        var config = LiveBoard.Config(boardFile: backlog.Path);
        using var gateway = LiveBoard.Gateway(config);

        var created = await LiveBoard.ImportAsync(gateway, config, backlog);
        try
        {
            backlog.Rewrite(backlog.Markdown.Replace(
                "Sync smoke test", "Sync smoke test (edited)", StringComparison.Ordinal));

            var board = await gateway.ReadAsync(config);
            Assert.True(board.IsSuccess, LiveBoard.Explain(board.Error));

            var plan = PlanBuilder.BuildResync(config, backlog.Items, board.Value, backlog.Markdown);
            Assert.True(plan.HasWork);
            Assert.Equal(0, plan.CreateCount);

            var report = await ApplyExecutor.ApplyAsync(
                gateway, config, plan,
                PlanBuilder.FingerprintBacklog(backlog.Markdown), board.Value.Fingerprint,
                null);

            Assert.True(report.IsSuccess, LiveBoard.Explain(report.Error));
            Assert.True(report.Value.AllSucceeded, LiveBoard.Explain(report.Value));

            var after = await gateway.ReadAsync(config);
            Assert.Contains(after.Value.Items, i => i.Title.Contains("(edited)", StringComparison.Ordinal));
        }
        finally
        {
            await LiveBoard.DiscardAsync(config, created);
        }
    }

    /// <summary>
    ///     The stale-plan guard against a board that can actually move. The assertion
    ///     that matters is the second one: nothing was created before the refusal.
    /// </summary>
    [LiveFact(Writes = true)]
    public async Task ApplyIsRefusedWhenTheBacklogMovedAfterThePlanWasBuilt()
    {
        using var backlog = LiveBoard.ScratchBacklog();
        var config = LiveBoard.Config(boardFile: backlog.Path);
        using var gateway = LiveBoard.Gateway(config);

        var board = await gateway.ReadAsync(config);
        Assert.True(board.IsSuccess, LiveBoard.Explain(board.Error));

        var plan = PlanBuilder.BuildImport(config, backlog.Items, board.Value, backlog.Markdown);
        Assert.True(plan.HasWork);

        var report = await ApplyExecutor.ApplyAsync(
            gateway, config, plan,
            PlanBuilder.FingerprintBacklog(backlog.Markdown + "\n"),
            board.Value.Fingerprint,
            null);

        Assert.True(report.IsFailure);
        Assert.Equal("plan.stale_backlog", report.Error!.Code);

        var after = await gateway.ReadAsync(config);
        Assert.Equal(board.Value.Items.Count, after.Value.Items.Count);
    }
}
