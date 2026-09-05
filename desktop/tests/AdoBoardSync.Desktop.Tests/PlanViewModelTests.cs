using AdoBoardSync.Core.Board;
using AdoBoardSync.Core.Markdown;
using AdoBoardSync.Core.Planning;
using AdoBoardSync.Core.Results;
using AdoBoardSync.Desktop.Services;
using AdoBoardSync.Desktop.ViewModels;
using AdoBoardSync.TestKit;

namespace AdoBoardSync.Desktop.Tests;

/// <summary>
///     The confirmation gate. `ApplyExecutor` refuses a stale Plan, but nothing there
///     stops a caller applying a Plan the user never confirmed — that guard lives
///     here, in <see cref="PlanViewModel.IsConfirming" />, and until these tests it
///     was the only part of the write path with no test at all.
///     Every assertion is about what reached the board, not about what the view model
///     says: the fake records each create and update, so "nothing was written" is
///     checked rather than assumed.
/// </summary>
public class PlanViewModelTests
{
    private const string Backlog = "## Epic 1\n\n### PROJ-101 · Do the thing\n\nSome description.\n";

    // ---------------------------------------------------- resync-tasks wiring

    private const string TaskBacklog =
        "## Epic 1\n\n### PROJ-101 · Do the thing\n\n- write the code\n";

    private static async Task<(PlanViewModel Plan, BacklogWorkspace Workspace, FakeBoardGateway Board)> ReadyAsync(
        Action<FakeBoardGateway>? arrange = null, string? markdown = null)
    {
        var board = new FakeBoardGateway();
        arrange?.Invoke(board);

        var profile = TempBoardProfile.Create(WriteBacklog(markdown));
        var workspace = await Shell.WorkspaceAsync(profile.ConfigPath);

        // A session token, so credential resolution is not what is under test.
        var plan = new PlanViewModel(_ => board) { SessionToken = "test-token" };
        return (plan, workspace, board);
    }

    private static string WriteBacklog(string? text = null)
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("abs-plan-").FullName, "backlog.md");
        File.WriteAllText(path, text ?? Backlog);
        return path;
    }

    private static void AssertNothingWritten(FakeBoardGateway board)
    {
        Assert.Empty(board.Created);
        Assert.Empty(board.Updated);
        Assert.Empty(board.Deleted);
    }

    [Fact]
    public async Task GeneratingAPlanWritesNothing()
    {
        var (plan, workspace, board) = await ReadyAsync();

        await plan.GenerateAsync(workspace);

        Assert.True(plan.HasPlan);
        Assert.True(plan.HasWork);
        AssertNothingWritten(board);
    }

    [Fact]
    public async Task APlanIsRefusedWhileTheBacklogHasUnsavedEdits()
    {
        // The file is the source of truth a Plan is computed from; edits that
        // exist only in the editor buffer must not be planned as if they were
        // on disk. The gate runs before anything — no board read, no gateway.
        var builds = 0;
        var board = new FakeBoardGateway();
        var plan = new PlanViewModel(_ =>
        {
            builds++;
            return board;
        }) { SessionToken = "test-token", UnsavedEditsCheck = () => true };

        var profile = TempBoardProfile.Create(WriteBacklog());
        var workspace = await Shell.WorkspaceAsync(profile.ConfigPath);

        await plan.GenerateAsync(workspace);

        Assert.True(plan.HasError);
        Assert.Contains("backlog.unsaved", plan.ErrorText);
        Assert.Equal(0, builds);
        AssertNothingWritten(board);
    }

    [Fact]
    public async Task AnApplyIsRefusedWhileTheBacklogHasUnsavedEdits()
    {
        var (plan, workspace, board) = await ReadyAsync();
        await plan.GenerateAsync(workspace);
        plan.RequestApply(workspace);
        Assert.True(plan.IsConfirming);

        plan.UnsavedEditsCheck = () => true;
        await plan.ApplyConfirmedAsync(workspace);

        Assert.True(plan.HasError);
        Assert.Contains("backlog.unsaved", plan.ErrorText);
        AssertNothingWritten(board);
    }

    [Fact]
    public async Task AskingToApplyWritesNothingUntilItIsConfirmed()
    {
        var (plan, workspace, board) = await ReadyAsync();
        await plan.GenerateAsync(workspace);

        plan.RequestApply(workspace);

        Assert.True(plan.IsConfirming);
        AssertNothingWritten(board);
    }

    /// <summary>
    ///     The guard itself. Calling Apply without the confirmation must do nothing —
    ///     if this check is ever removed, every other test still passes.
    /// </summary>
    [Fact]
    public async Task ApplyingWithoutConfirmingWritesNothing()
    {
        var (plan, workspace, board) = await ReadyAsync();
        await plan.GenerateAsync(workspace);

        Assert.False(plan.IsConfirming);
        await plan.ApplyConfirmedAsync(workspace);

        AssertNothingWritten(board);
        Assert.True(plan.HasPlan);
    }

    [Fact]
    public async Task CancellingClosesTheConfirmationAndWritesNothing()
    {
        var (plan, workspace, board) = await ReadyAsync();
        await plan.GenerateAsync(workspace);
        plan.RequestApply(workspace);

        plan.CancelApply();
        await plan.ApplyConfirmedAsync(workspace);

        Assert.False(plan.IsConfirming);
        AssertNothingWritten(board);
    }

    [Fact]
    public async Task ConfirmingWritesExactlyThePlannedRows()
    {
        var (plan, workspace, board) = await ReadyAsync();
        await plan.GenerateAsync(workspace);
        var planned = plan.Plan!.WriteRows.Count;

        plan.RequestApply(workspace);
        await plan.ApplyConfirmedAsync(workspace);

        Assert.Equal(planned, board.Created.Count);
        Assert.Empty(board.Updated);

        // The Plan is spent: the board has moved, so a further write needs a new one.
        Assert.False(plan.HasPlan);
        Assert.False(plan.IsConfirming);
    }

    /// <summary>PRD-AC-04: the counts are restated before anything is written.</summary>
    [Fact]
    public async Task TheConfirmationRestatesWhatWouldBeCreated()
    {
        var (plan, workspace, _) = await ReadyAsync();
        await plan.GenerateAsync(workspace);

        Assert.Equal(
            $"Create {plan.Plan!.CreateCount} work items in Azure DevOps?",
            plan.ConfirmQuestion);
    }

    [Fact]
    public async Task TheConfirmationRestatesWhatWouldBeUpdated()
    {
        var (plan, workspace, _) = await ReadyAsync(board => board.Items.Add(new BoardWorkItem
        {
            Id = 7,
            Title = "PROJ-101 · A stale title",
            WorkItemType = "Issue",
            Description = "<p>Stale.</p>"
        }));

        plan.Choose(PlanCommand.Resync);
        await plan.GenerateAsync(workspace);

        Assert.Contains("Update", plan.ConfirmQuestion, StringComparison.Ordinal);
        Assert.Contains("Azure DevOps?", plan.ConfirmQuestion, StringComparison.Ordinal);
    }

    /// <summary>One item must read "1 work item", not "1 work items".</summary>
    [Fact]
    public async Task TheConfirmationAgreesInNumber()
    {
        var (plan, workspace, board) = await ReadyAsync(b => b.Items.Add(new BoardWorkItem
        {
            Id = 7,
            Title = "Epic 1",
            WorkItemType = "Epic",
            Description = string.Empty
        }));

        await plan.GenerateAsync(workspace);

        Assert.Equal(1, plan.Plan!.CreateCount);
        Assert.Equal("Create 1 work item in Azure DevOps?", plan.ConfirmQuestion);
        AssertNothingWritten(board);
    }

    /// <summary>A Plan belongs to the command that produced it.</summary>
    [Fact]
    public async Task SwitchingCommandDiscardsThePlanAndAnyConfirmation()
    {
        var (plan, workspace, board) = await ReadyAsync();
        await plan.GenerateAsync(workspace);
        plan.RequestApply(workspace);

        plan.Choose(PlanCommand.Resync);

        Assert.False(plan.HasPlan);
        Assert.False(plan.IsConfirming);
        Assert.Empty(plan.Rows);

        await plan.ApplyConfirmedAsync(workspace);
        AssertNothingWritten(board);
    }

    /// <summary>A board that moved between the review and the Apply is refused.</summary>
    [Fact]
    public async Task AConfirmedApplyIsStillRefusedWhenTheBoardMoved()
    {
        var (plan, workspace, board) = await ReadyAsync();
        await plan.GenerateAsync(workspace);
        plan.RequestApply(workspace);

        board.Items.Add(new BoardWorkItem
        {
            Id = 99,
            Title = "Something somebody else added",
            WorkItemType = "Issue",
            Description = string.Empty
        });

        await plan.ApplyConfirmedAsync(workspace);

        AssertNothingWritten(board);
        Assert.True(plan.HasError);
        Assert.Contains("plan.stale_board", plan.ErrorText!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReadFailureIsReportedWithoutAPlanOrAWrite()
    {
        var (plan, workspace, board) = await ReadyAsync(b =>
            b.ReadError = Error.Authorization("board.unauthorized", "Rejected."));

        await plan.GenerateAsync(workspace);

        Assert.False(plan.HasPlan);
        Assert.True(plan.HasError);
        AssertNothingWritten(board);
    }

    /// <summary>
    ///     Only a session token is used here; with none set the resolver falls back to
    ///     the profile's environment variable and token file, and reports what it tried.
    /// </summary>
    [Fact]
    public async Task WithNoTokenAnywhereNothingIsReadAndNothingIsWritten()
    {
        var (_, workspace, board) = await ReadyAsync();
        var plan = new PlanViewModel(_ => board);

        await plan.GenerateAsync(workspace);

        Assert.False(plan.HasPlan);
        Assert.True(plan.HasError);
        Assert.Equal(0, board.ReadCount);
        AssertNothingWritten(board);
    }

    [Fact]
    public async Task TheTasksCommandPlansCreatesDeletesAndTheirParents()
    {
        var (plan, workspace, board) = await ReadyAsync(
            b =>
            {
                var issue = b.Seed("Issue", "PROJ-101 · Do the thing");
                b.Seed("Task", "a bullet that left the backlog", parentId: issue);
            },
            TaskBacklog);

        plan.Choose(PlanCommand.ResyncTasks);
        await plan.GenerateAsync(workspace);

        Assert.True(plan.IsResyncTasks);
        Assert.True(plan.HasPlan);
        Assert.True(plan.HasWork);
        Assert.Contains(plan.Rows, r => r.IsDelete);
        Assert.Contains(plan.Rows, r => r.IsCreate && r.ParentBoardId is not null);
        AssertNothingWritten(board); // generating never writes
    }

    [Fact]
    public async Task ConfirmingTheTasksPlanAppliesCreatesAndDeletesTogether()
    {
        var (plan, workspace, board) = await ReadyAsync(
            b =>
            {
                var issue = b.Seed("Issue", "PROJ-101 · Do the thing");
                b.Seed("Task", "a bullet that left the backlog", parentId: issue);
            },
            TaskBacklog);

        plan.Choose(PlanCommand.ResyncTasks);
        await plan.GenerateAsync(workspace);
        plan.RequestApply(workspace);
        await plan.ApplyConfirmedAsync(workspace);

        Assert.Single(board.Deleted);
        var created = Assert.Single(board.Created);
        Assert.Equal("Task", created.Type);
        Assert.NotNull(created.ParentId);
        Assert.Equal(MarkdownHtml.Inline("write the code"), created.Description);
    }

    /// <summary>PRD-AC-04 for deletes: both counts are named before anything moves.</summary>
    [Fact]
    public async Task TheTasksConfirmationRestatesBothKindsOfChange()
    {
        var (plan, workspace, _) = await ReadyAsync(
            b =>
            {
                var issue = b.Seed("Issue", "PROJ-101 · Do the thing");
                b.Seed("Task", "a bullet that left the backlog", parentId: issue);
            },
            TaskBacklog);

        plan.Choose(PlanCommand.ResyncTasks);
        await plan.GenerateAsync(workspace);

        Assert.Equal("Create 1 task and delete 1 in Azure DevOps?", plan.ConfirmQuestion);
    }

    [Fact]
    public async Task SwitchingToTheTasksCommandDiscardsThePreviousPlan()
    {
        var (plan, workspace, board) = await ReadyAsync();
        await plan.GenerateAsync(workspace);
        plan.RequestApply(workspace);

        plan.Choose(PlanCommand.ResyncTasks);

        Assert.False(plan.HasPlan);
        Assert.Empty(plan.Rows);
        await plan.ApplyConfirmedAsync(workspace);
        AssertNothingWritten(board);
    }

    // ------------------------------------------------------ markup gate (AC-03)

    /// <summary>A copy of the loaded workspace carrying an audited problem count.</summary>
    private static BacklogWorkspace WithProblems(BacklogWorkspace source, int count)
    {
        return source with { MarkupProblemCount = count };
    }

    [Fact]
    public async Task AConfirmationIsNeverOfferedWhileBacklogMarkupIsMalformed()
    {
        var (plan, workspace, board) = await ReadyAsync(markdown: TaskBacklog);
        await plan.GenerateAsync(workspace);

        var broken = WithProblems(workspace, 2);
        plan.RequestApply(broken);

        Assert.False(plan.IsConfirming); // no dialog was offered
        Assert.True(plan.HasError);
        Assert.Contains("markup.invalid", plan.ErrorText!, StringComparison.Ordinal);
        Assert.Equal(1, board.ReadCount); // only the Plan's own read ran
        AssertNothingWritten(board);
    }

    [Fact]
    public async Task TheApplyPathRefusesAgainEvenIfAConfirmationWasObtained()
    {
        // Defence in depth: the confirmation was granted against a clean audit,
        // then the apply is handed a profile whose markup no longer passes. The
        // second guard must hold on its own.
        var (plan, workspace, board) = await ReadyAsync(markdown: TaskBacklog);
        await plan.GenerateAsync(workspace);
        plan.RequestApply(workspace);
        Assert.True(plan.IsConfirming);

        var sinceBroken = WithProblems(workspace, 1);
        await plan.ApplyConfirmedAsync(sinceBroken);

        Assert.False(plan.IsConfirming);
        Assert.True(plan.HasError);
        Assert.Contains("markup.invalid", plan.ErrorText!, StringComparison.Ordinal);
        AssertNothingWritten(board);
    }

    [Fact]
    public async Task ResolvingTheMarkupUnblocksApply()
    {
        var (plan, workspace, board) = await ReadyAsync(markdown: TaskBacklog);
        await plan.GenerateAsync(workspace);

        plan.RequestApply(WithProblems(workspace, 1));
        Assert.False(plan.IsConfirming);

        // The user fixes the file and regenerates: a clean audit confirms again.
        await plan.GenerateAsync(workspace);
        plan.RequestApply(workspace);

        Assert.True(plan.IsConfirming);
        Assert.False(plan.HasError);
        plan.CancelApply();
        AssertNothingWritten(board);
    }
}
