using AdoBoardSync.Core.Backlog;
using AdoBoardSync.Core.Board;
using AdoBoardSync.Core.Configuration;
using AdoBoardSync.Core.Planning;

namespace AdoBoardSync.Desktop.Tests;

/// <summary>
/// Pins the rules ported from the CLI's <c>dedup</c>, <c>sprints</c>,
/// <c>assign</c>, <c>close-children</c> and <c>sync-one</c>. Each of these
/// changes ownership, scheduling or state rather than structure, and each rule
/// here is one the CLI would be wrong to lose.
/// </summary>
public class PlanBuilderLifecycleTests
{
    private const string Markdown = "## Epic 1\n\n### PROJ-101 · A\n";

    private static BoardConfig Config(string extra = "")
    {
        var json = $$"""
            {"org":"o","project":"p","code_prefix":"PROJ","board_file":"backlog.md"{{extra}}}
            """;

        return BoardConfig.Parse(json, Path.GetTempPath()).Value;
    }

    private static BoardWorkItem Work(
        int id,
        string type,
        string title,
        int? parentId = null,
        string state = "New",
        string assignedTo = "",
        string iterationPath = "",
        string description = "") => new()
    {
        Id = id,
        Title = title,
        WorkItemType = type,
        ParentId = parentId,
        State = state,
        AssignedTo = assignedTo,
        IterationPath = iterationPath,
        Description = description,
    };

    // ----------------------------------------------------------------- dedup

    [Fact]
    public void DedupKeepsTheLowestIdOfEachDuplicateSet()
    {
        var snapshot = BoardSnapshot.From([
            Work(7, "Epic", "Epic 1"),
            Work(3, "Epic", "epic 1"),
            Work(9, "Issue", "PROJ-101 · A"),
            Work(4, "Issue", "PROJ-101 · A copy"),
        ]);

        var plan = PlanBuilder.BuildDedup(Config(), snapshot, Markdown);

        Assert.Equal(PlanCommand.Dedup, plan.Command);
        Assert.All(plan.Rows, row => Assert.Equal(PlanOperation.Delete, row.Operation));
        Assert.Equal([7, 9], plan.Rows.Select(r => r.BoardId).Order());
    }

    [Fact]
    public void DedupTakesAChildTaskWithTheDuplicateParentItDeletes()
    {
        // A plain work-item DELETE does not remove children, so without the
        // cascade the Tasks under a removed duplicate are left orphaned.
        var snapshot = BoardSnapshot.From([
            Work(1, "Issue", "PROJ-101 · A"),
            Work(2, "Issue", "PROJ-101 · A again"),
            Work(3, "Task", "Do the thing", parentId: 2),
        ]);

        var plan = PlanBuilder.BuildDedup(Config(), snapshot, Markdown);

        Assert.Equal([2, 3], plan.Rows.Select(r => r.BoardId).Order());
        Assert.Contains(plan.Rows, r => r.BoardId == 3 && r.ChangeSummary.Length > 0);
    }

    [Fact]
    public void DedupTreatsTwoTasksWithOneTitleUnderOneParentAsDuplicates()
    {
        var snapshot = BoardSnapshot.From([
            Work(1, "Issue", "PROJ-101 · A"),
            Work(5, "Task", "Do the thing", parentId: 1),
            Work(6, "Task", "do the THING", parentId: 1),
        ]);

        var plan = PlanBuilder.BuildDedup(Config(), snapshot, Markdown);

        Assert.Equal([6], plan.Rows.Select(r => r.BoardId));
    }

    [Fact]
    public void DedupLeavesTheSameTitleUnderTwoDifferentParentsAlone()
    {
        // "Write the tests" under two Issues is two pieces of work, not one
        // duplicated. Grouping tasks globally rather than per parent would delete
        // real work.
        var snapshot = BoardSnapshot.From([
            Work(1, "Issue", "PROJ-101 · A"),
            Work(2, "Issue", "PROJ-102 · B"),
            Work(5, "Task", "Write the tests", parentId: 1),
            Work(6, "Task", "Write the tests", parentId: 2),
        ]);

        var plan = PlanBuilder.BuildDedup(Config(), snapshot, Markdown);

        Assert.Empty(plan.Rows);
    }

    [Fact]
    public void DedupIsQuietOnABoardWithNoDuplicates()
    {
        var snapshot = BoardSnapshot.From([
            Work(1, "Epic", "Epic 1"),
            Work(2, "Issue", "PROJ-101 · A"),
        ]);

        var plan = PlanBuilder.BuildDedup(Config(), snapshot, Markdown);

        Assert.Empty(plan.Rows);
        Assert.Equal("no duplicates on the board", plan.Summary);
    }

    // --------------------------------------------------------------- sprints

    private const string TwoSprints =
        ""","iterations":[{"name":"S1","start":"2026-01-05","finish":"2026-01-16","items":["PROJ-101"]},{"name":"S2","items":["PROJ-102"]}]""";

    [Fact]
    public void SprintsPlansANodePerConfiguredIterationAndMovesTheIssuesIntoThem()
    {
        var snapshot = BoardSnapshot.From([
            Work(1, "Issue", "PROJ-101 · A"),
            Work(2, "Issue", "PROJ-102 · B"),
        ]);

        var plan = PlanBuilder.BuildSprints(Config(TwoSprints), snapshot, Markdown);

        var nodes = plan.Rows.Where(r => r.Target == PlanTarget.IterationNode).ToArray();
        Assert.Equal(["S1", "S2"], nodes.Select(n => n.Title));
        Assert.Equal("2026-01-05", nodes[0].Iteration!.Start);

        var moves = plan.Rows.Where(r => r.Target == PlanTarget.WorkItem).ToArray();
        Assert.Equal(2, moves.Length);
        Assert.Equal(@"p\S1", moves.Single(m => m.Code == "PROJ-101").Changes[0].After);
    }

    [Fact]
    public void ASprintRowIsBadgedAsASprintRatherThanBorrowingTheEpicBadge()
    {
        // It creates a classification node, not a work item. Badging it EPIC
        // would put a row in the review that names a thing the Plan never touches.
        var plan = PlanBuilder.BuildSprints(
            Config(TwoSprints), BoardSnapshot.From([]), Markdown);

        Assert.Equal("SPRINT", plan.Rows[0].Badge);
    }

    [Fact]
    public void SprintsLeavesAnIssueThatIsAlreadyInItsSprintAlone()
    {
        // Azure DevOps echoes the path back with the project's own casing, so the
        // comparison is case-insensitive. Comparing ordinally would rewrite every
        // item on every run.
        var snapshot = BoardSnapshot.From([
            Work(1, "Issue", "PROJ-101 · A", iterationPath: @"P\s1"),
        ]);

        var plan = PlanBuilder.BuildSprints(Config(TwoSprints), snapshot, Markdown, assignOnly: true);

        Assert.Empty(plan.Rows);
    }

    [Fact]
    public void SprintsCascadesToChildTasksUnlessAskedNotTo()
    {
        var snapshot = BoardSnapshot.From([
            Work(1, "Issue", "PROJ-101 · A"),
            Work(2, "Task", "Do the thing", parentId: 1),
        ]);

        var withTasks = PlanBuilder.BuildSprints(
            Config(TwoSprints), snapshot, Markdown, assignOnly: true);
        var withoutTasks = PlanBuilder.BuildSprints(
            Config(TwoSprints), snapshot, Markdown, assignOnly: true, includeTasks: false);

        Assert.Equal([1, 2], withTasks.Rows.Select(r => r.BoardId).Order());
        Assert.Equal([1], withoutTasks.Rows.Select(r => r.BoardId));
    }

    [Fact]
    public void SprintsGivesTheFirstListedIterationACodeThatAppearsInTwo()
    {
        var config = Config(
            ""","iterations":[{"name":"S1","items":["PROJ-101"]},{"name":"S2","items":["PROJ-101"]}]""");
        var snapshot = BoardSnapshot.From([Work(1, "Issue", "PROJ-101 · A")]);

        var plan = PlanBuilder.BuildSprints(config, snapshot, Markdown, assignOnly: true);

        Assert.Equal(@"p\S1", Assert.Single(plan.Rows).Changes[0].After);
    }

    [Fact]
    public void SprintsSaysWhichConfiguredCodesAreNotOnTheBoardAndWhichBoardIssuesNoSprintClaims()
    {
        var snapshot = BoardSnapshot.From([Work(1, "Issue", "PROJ-999 · Unclaimed")]);

        var plan = PlanBuilder.BuildSprints(Config(TwoSprints), snapshot, Markdown, assignOnly: true);

        Assert.Contains(plan.Notes, n => n.Contains("PROJ-101") && n.Contains("not on the board"));
        Assert.Contains(plan.Notes, n => n.Contains("PROJ-999") && n.Contains("no sprint"));
    }

    [Fact]
    public void SprintsWithNoIterationsConfiguredExplainsItselfRatherThanPlanningNothing()
    {
        var plan = PlanBuilder.BuildSprints(Config(), BoardSnapshot.From([]), Markdown);

        Assert.Empty(plan.Rows);
        Assert.Contains(plan.Notes, n => n.Contains("No iterations configured"));
    }

    // ---------------------------------------------------------------- assign

    private const string OneOwner = ""","assignees":{"ada@example.com":["PROJ-101"]}""";

    [Fact]
    public void AssignSetsTheConfiguredOwnerOnTheIssueAndItsTasks()
    {
        var snapshot = BoardSnapshot.From([
            Work(1, "Issue", "PROJ-101 · A"),
            Work(2, "Task", "Do the thing", parentId: 1),
        ]);

        var plan = PlanBuilder.BuildAssign(Config(OneOwner), snapshot, Markdown);

        Assert.Equal([1, 2], plan.Rows.Select(r => r.BoardId).Order());
        Assert.All(plan.Rows, r => Assert.Equal("ada@example.com", r.Changes[0].After));
        Assert.Equal("(unassigned)", plan.Rows[0].Changes[0].Before);
    }

    [Fact]
    public void AssignShowsAnItemThatAlreadyHasTheWantedOwnerAsUnchangedAndWritesNothingForIt()
    {
        // Shown, not omitted (PRD-AC-12): a plan that silently drops the codes it
        // has nothing to do for is indistinguishable from one that lost them.
        // Unchanged rows never reach the board, which HasWork is what proves.
        var snapshot = BoardSnapshot.From([
            Work(1, "Issue", "PROJ-101 · A", assignedTo: "ADA@example.com"),
        ]);

        var plan = PlanBuilder.BuildAssign(Config(OneOwner), snapshot, Markdown);

        var row = Assert.Single(plan.Rows);
        Assert.Equal(PlanOperation.Unchanged, row.Operation);
        Assert.Equal("PROJ-101", row.Code);
        Assert.Empty(plan.WriteRows);
        Assert.False(plan.HasWork);
        Assert.Equal(1, plan.UnchangedCount);
    }

    [Fact]
    public void AssignOnlyUnassignedNeverTakesAnItemFromSomebodyElse()
    {
        var snapshot = BoardSnapshot.From([
            Work(1, "Issue", "PROJ-101 · A", assignedTo: "grace@example.com"),
        ]);

        var overwrite = PlanBuilder.BuildAssign(Config(OneOwner), snapshot, Markdown);
        var fillOnly = PlanBuilder.BuildAssign(
            Config(OneOwner), snapshot, Markdown, onlyUnassigned: true);

        // Overwriting plans the write; filling only unassigned items reports the
        // item as untouched rather than dropping it from the review entirely.
        Assert.Equal(PlanOperation.Update, Assert.Single(overwrite.Rows).Operation);
        Assert.Equal(PlanOperation.Unchanged, Assert.Single(fillOnly.Rows).Operation);
        Assert.False(fillOnly.HasWork);
    }

    [Fact]
    public void AssignWithNoAssigneesConfiguredExplainsItself()
    {
        var plan = PlanBuilder.BuildAssign(Config(), BoardSnapshot.From([]), Markdown);

        Assert.Empty(plan.Rows);
        Assert.Contains(plan.Notes, n => n.Contains("No assignees configured"));
    }

    // -------------------------------------------------------- close-children

    [Fact]
    public void CloseChildrenClosesEveryOpenDescendantOfADoneItemAtAnyDepth()
    {
        // Azure DevOps propagates state upward but never downward, so a Done Epic
        // sits above open Issues and Tasks indefinitely.
        var snapshot = BoardSnapshot.From([
            Work(1, "Epic", "Epic 1", state: "Done"),
            Work(2, "Issue", "PROJ-101 · A", parentId: 1, state: "Active"),
            Work(3, "Task", "Do the thing", parentId: 2, state: "New"),
        ]);

        var plan = PlanBuilder.BuildCloseChildren(Config(), snapshot, Markdown);

        Assert.Equal([2, 3], plan.Rows.Select(r => r.BoardId).Order());
        Assert.All(plan.Rows, r => Assert.Equal("Done", r.Changes[0].After));
        Assert.All(plan.Rows, r => Assert.Equal(1, r.ParentBoardId));
    }

    [Fact]
    public void CloseChildrenIgnoresAnOpenItemWhoseAncestorsAreAllOpen()
    {
        var snapshot = BoardSnapshot.From([
            Work(1, "Epic", "Epic 1", state: "Active"),
            Work(2, "Issue", "PROJ-101 · A", parentId: 1, state: "Active"),
        ]);

        Assert.Empty(PlanBuilder.BuildCloseChildren(Config(), snapshot, Markdown).Rows);
    }

    [Fact]
    public void CloseChildrenCopiesTheDoneParentsAssigneeOnlyOntoUnassignedItems()
    {
        var snapshot = BoardSnapshot.From([
            Work(1, "Epic", "Epic 1", state: "Done", assignedTo: "ada@example.com"),
            Work(2, "Issue", "PROJ-101 · A", parentId: 1, state: "Active"),
            Work(3, "Issue", "PROJ-102 · B", parentId: 1, state: "Active", assignedTo: "grace@example.com"),
        ]);

        var plan = PlanBuilder.BuildCloseChildren(Config(), snapshot, Markdown, assignFromParent: true);

        var filled = plan.Rows.Single(r => r.BoardId == 2);
        var kept = plan.Rows.Single(r => r.BoardId == 3);

        Assert.Contains(filled.Changes, c => c.Field == BoardFieldChange.AssignedToField);
        Assert.DoesNotContain(kept.Changes, c => c.Field == BoardFieldChange.AssignedToField);
    }

    [Fact]
    public void CloseChildrenDoesNotAssignUnlessAsked()
    {
        var snapshot = BoardSnapshot.From([
            Work(1, "Epic", "Epic 1", state: "Done", assignedTo: "ada@example.com"),
            Work(2, "Issue", "PROJ-101 · A", parentId: 1, state: "Active"),
        ]);

        var plan = PlanBuilder.BuildCloseChildren(Config(), snapshot, Markdown);

        Assert.DoesNotContain(
            Assert.Single(plan.Rows).Changes, c => c.Field == BoardFieldChange.AssignedToField);
    }

    [Fact]
    public void CloseChildrenSurvivesABoardThatClaimsAParentCycle()
    {
        // A cycle cannot happen through the Azure DevOps UI, but a Plan that hangs
        // is worse than one that reports nothing.
        var snapshot = BoardSnapshot.From([
            Work(1, "Issue", "PROJ-101 · A", parentId: 2, state: "Active"),
            Work(2, "Issue", "PROJ-102 · B", parentId: 1, state: "Active"),
        ]);

        Assert.Empty(PlanBuilder.BuildCloseChildren(Config(), snapshot, Markdown).Rows);
    }

    // -------------------------------------------------------------- sync-one

    private static IReadOnlyList<BacklogItem> Backlog() =>
    [
        new BacklogItem { Level = BacklogLevel.Epic, Title = "Epic 1" },
        new BacklogItem
        {
            Level = BacklogLevel.Issue,
            Title = "PROJ-101 · A",
            Code = "PROJ-101",
            DescriptionLines = ["Body."],
        },
    ];

    [Fact]
    public void SyncOneUpdatesTheOneIssueAndSetsItsSprint()
    {
        var snapshot = BoardSnapshot.From([
            Work(1, "Epic", "Epic 1"),
            Work(2, "Issue", "PROJ-101 · Stale title", parentId: 1),
        ]);

        var plan = PlanBuilder.BuildSyncOne(
            Config(TwoSprints), Backlog(), snapshot, Markdown, "proj-101", "S1");

        Assert.True(plan.IsSuccess);
        var row = Assert.Single(plan.Value.Rows);
        Assert.Equal(PlanOperation.Update, row.Operation);
        Assert.Contains(row.Changes, c => c.Field == BoardFieldChange.TitleField);
        Assert.Contains(row.Changes, c =>
            c.Field == BoardFieldChange.IterationPathField && c.After == @"p\S1");
    }

    [Fact]
    public void SyncOneCreatesTheIssueUnderItsBacklogEpicWhenTheBoardLacksIt()
    {
        var snapshot = BoardSnapshot.From([Work(1, "Epic", "Epic 1")]);

        var plan = PlanBuilder.BuildSyncOne(
            Config(TwoSprints), Backlog(), snapshot, Markdown, "PROJ-101", "S1");

        var row = Assert.Single(plan.Value.Rows);
        Assert.Equal(PlanOperation.Create, row.Operation);
        Assert.Equal(1, row.ParentBoardId);
    }

    [Fact]
    public void SyncOneIgnoresATaskThatMerelyCitesTheCodeInItsTitle()
    {
        // "…surfaced to monitoring (PROJ-101)" on a Task is not the PROJ-101
        // Issue. An unscoped match made the cited ticket unsyncable.
        var snapshot = BoardSnapshot.From([
            Work(1, "Epic", "Epic 1"),
            Work(2, "Issue", "PROJ-101 · A", parentId: 1),
            Work(3, "Task", "Rejections are logged (PROJ-101)", parentId: 2),
        ]);

        var plan = PlanBuilder.BuildSyncOne(
            Config(TwoSprints), Backlog(), snapshot, Markdown, "PROJ-101", "S1");

        Assert.True(plan.IsSuccess);
        Assert.Equal(2, Assert.Single(plan.Value.Rows).BoardId);
    }

    [Fact]
    public void SyncOneRefusesACodeThatIsNotAnIssueCode()
    {
        var plan = PlanBuilder.BuildSyncOne(
            Config(TwoSprints), Backlog(), BoardSnapshot.From([]), Markdown, "PROJ-1O1", "S1");

        Assert.Equal("syncone.bad_code", plan.Error!.Code);
    }

    [Fact]
    public void SyncOneRefusesASprintThatIsNotInTheConfig()
    {
        var plan = PlanBuilder.BuildSyncOne(
            Config(TwoSprints), Backlog(), BoardSnapshot.From([]), Markdown, "PROJ-101", "S9");

        Assert.Equal("syncone.unknown_sprint", plan.Error!.Code);
    }

    [Fact]
    public void SyncOneRefusesACodeTheBacklogDoesNotHold()
    {
        var plan = PlanBuilder.BuildSyncOne(
            Config(TwoSprints), Backlog(), BoardSnapshot.From([]), Markdown, "PROJ-999", "S1");

        Assert.Equal("syncone.not_in_backlog", plan.Error!.Code);
    }

    [Fact]
    public void SyncOneRefusesRatherThanGuessingBetweenTwoMatchingBoardItems()
    {
        var snapshot = BoardSnapshot.From([
            Work(1, "Epic", "Epic 1"),
            Work(2, "Issue", "PROJ-101 · A"),
            Work(3, "Issue", "PROJ-101 · A again"),
        ]);

        var plan = PlanBuilder.BuildSyncOne(
            Config(TwoSprints), Backlog(), snapshot, Markdown, "PROJ-101", "S1");

        Assert.Equal("syncone.ambiguous", plan.Error!.Code);
    }

    [Fact]
    public void SyncOneReportsAnIssueThatAlreadyMatchesAsUnchanged()
    {
        var config = Config(TwoSprints);
        var html = Core.Markdown.MarkdownHtml.ToHtml(["Body."]);
        var snapshot = BoardSnapshot.From([
            Work(1, "Epic", "Epic 1"),
            Work(2, "Issue", "PROJ-101 · A", parentId: 1, iterationPath: @"p\S1", description: html),
        ]);

        var plan = PlanBuilder.BuildSyncOne(config, Backlog(), snapshot, Markdown, "PROJ-101", "S1");

        Assert.Equal(PlanOperation.Unchanged, Assert.Single(plan.Value.Rows).Operation);
        Assert.False(plan.Value.HasWork);
    }
}
