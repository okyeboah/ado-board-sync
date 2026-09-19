using AdoBoardSync.Core.Backlog;
using AdoBoardSync.Core.Board;
using AdoBoardSync.Core.Planning;
using AdoBoardSync.Desktop.ViewModels;

namespace AdoBoardSync.Desktop.Tests;

/// <summary>
///     View-model behaviour for the commands the first table grew: what the
///     confirmation restates, which inputs each command asks for, and that typed
///     inputs are refused before anything is read. The class is partial because the
///     original file had no line budget left.
/// </summary>
public sealed partial class PlanViewModelTests
{
    private const string StateBacklog = "## Epic 1\n\n### PROJ-101 · A\n";

    private static Plan SetStatePlan(int updateCount, int unchangedCount = 0)
    {
        var rows = new List<PlanRow>();
        for (var i = 0; i < updateCount; i++)
            rows.Add(new PlanRow
            {
                Operation = PlanOperation.Update,
                Level = BacklogLevel.Issue,
                Title = $"PROJ-10{i} · A",
                Code = $"PROJ-10{i}",
                BoardId = 5 + i,
                Changes = [new PlanFieldChange(BoardFieldChange.StateField, "New", "Done")]
            });

        for (var i = 0; i < unchangedCount; i++)
            rows.Add(new PlanRow
            {
                Operation = PlanOperation.Unchanged,
                Level = BacklogLevel.Issue,
                Title = $"PROJ-20{i} · B",
                Code = $"PROJ-20{i}",
                BoardId = 90 + i
            });

        return new Plan
        {
            Command = PlanCommand.SetState,
            Rows = rows,
            BacklogFingerprint = "b",
            BoardFingerprint = "d"
        };
    }

    [Fact]
    public void TheCatalogOffersEveryCommandTheBuilderCanPlan()
    {
        // Collectively exhaustive by construction: a new PlanCommand with no
        // catalog row would leave the selector unable to choose it.
        var planned = Enum.GetValues<PlanCommand>().ToHashSet();

        Assert.Equal(planned, PlanCommandCatalog.All.Select(c => c.Command).ToHashSet());
    }

    [Fact]
    public void SetStateAsksForIdsAndTheirOptionsAndNothingElse()
    {
        var plan = new PlanViewModel();

        plan.Choose(PlanCommand.SetState);

        Assert.True(plan.NeedsIds);
        Assert.True(plan.SelectedCommand.SupportsTargetState);
        Assert.True(plan.SelectedCommand.SupportsNoTick);
        Assert.False(plan.NeedsCode);
        Assert.False(plan.NeedsSprint);
        Assert.False(plan.NeedsRepos);
    }

    [Fact]
    public void AdvanceAsksForRepositoriesAndTheirOptionsAndNothingElse()
    {
        var plan = new PlanViewModel();

        plan.Choose(PlanCommand.Advance);

        Assert.True(plan.NeedsRepos);
        Assert.Equal("origin/main", plan.BaseRef);
        Assert.True(plan.Fetch);
        Assert.False(plan.NeedsIds);
        Assert.False(plan.NeedsCode);
    }

    [Fact]
    public void TheSetStateConfirmationRestatesCountsAndTargetState()
    {
        var board = new FakeBoardGateway();
        var plan = new PlanViewModel(_ => board)
        {
            Command = PlanCommand.SetState,
            Plan = SetStatePlan(2, 1)
        };

        Assert.Contains("2", plan.ConfirmQuestion);
        Assert.Contains("terminal state", plan.ConfirmQuestion);

        plan.TargetState = "Closed";
        Assert.Contains("'Closed'", plan.ConfirmQuestion);
    }

    [Fact]
    public void TheSyncConfirmationNamesAllThreeKindsOfChange()
    {
        var plan = new PlanViewModel
        {
            Command = PlanCommand.Sync,
            Plan = new Plan
            {
                Command = PlanCommand.Sync,
                Rows =
                [
                    new PlanRow { Operation = PlanOperation.Create, Level = BacklogLevel.Epic, Title = "E" },
                    new PlanRow
                    {
                        Operation = PlanOperation.Update, Level = BacklogLevel.Issue, Title = "I", BoardId = 1
                    },
                    new PlanRow
                    {
                        Operation = PlanOperation.Delete, Level = BacklogLevel.Issue, Title = "T", BoardId = 2
                    }
                ],
                BacklogFingerprint = "b",
                BoardFingerprint = "d"
            }
        };

        Assert.Contains("Create 1", plan.ConfirmQuestion);
        Assert.Contains("update 1", plan.ConfirmQuestion);
        Assert.Contains("delete 1", plan.ConfirmQuestion);
    }

    [Fact]
    public async Task SetStateWithoutIdsIsRefusedBeforeAnythingIsRead()
    {
        var board = new FakeBoardGateway();
        var plan = new PlanViewModel(_ => board);
        plan.Choose(PlanCommand.SetState);

        // The workspace is untouched by this route: the ids are checked before
        // the credential is resolved, matching the CLI's argument parsing.
        await plan.GenerateAsync(null!);

        Assert.True(plan.HasError);
        Assert.Contains("setstate.no_ids", plan.ErrorText);
        Assert.False(plan.HasPlan);
        Assert.False(board.ReadCount > 0);
    }

    [Fact]
    public async Task AdvanceWithoutRepositoriesIsRefusedBeforeAnythingIsRead()
    {
        var board = new FakeBoardGateway();
        var plan = new PlanViewModel(_ => board);
        plan.Choose(PlanCommand.Advance);

        await plan.GenerateAsync(null!);

        Assert.True(plan.HasError);
        Assert.Contains("advance.no_repo", plan.ErrorText);
        Assert.False(plan.HasPlan);
        Assert.False(board.ReadCount > 0);
    }
}