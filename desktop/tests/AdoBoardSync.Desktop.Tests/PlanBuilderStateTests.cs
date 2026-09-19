using AdoBoardSync.Core.Backlog;
using AdoBoardSync.Core.Board;
using AdoBoardSync.Core.Configuration;
using AdoBoardSync.Core.Planning;
using AdoBoardSync.Core.Results;

namespace AdoBoardSync.Desktop.Tests;

/// <summary>
///     The three commands the first command table grew: the CLI's <c>sync</c>
///     composite (FSD §3.3), <c>set-state</c> and <c>advance</c> — plus the
///     <c>--reset-on-missing</c> recovery they share Apply with. Each rule here is
///     one the CLI would be wrong to lose.
/// </summary>
public class PlanBuilderStateTests
{
    private const string Markdown = "## Epic 1\n\n### PROJ-101 · A\n";

    // --------------------------------------------------------------- advance

    private const string TodoDoing =
        ""","states":{"todo":"To Do","doing":"Doing"}""";

    // ------------------------------------------- sprints reset-on-missing

    private const string OneSprint =
        ""","iterations":[{"name":"S1","items":["PROJ-101"]}]""";

    private static BoardConfig Config(string extra = "")
    {
        var json = $$"""
                     {"org":"o","project":"p","code_prefix":"PROJ","board_file":"backlog.md"{{extra}}}
                     """;

        return BoardConfig.Parse(json, Path.GetTempPath()).Value;
    }

    private static BacklogItem Issue(string code, string title)
    {
        return new BacklogItem
        {
            Level = BacklogLevel.Issue,
            Title = title,
            Code = code
        };
    }

    private static BacklogItem IssueWithBullets(string code, string title, params string[] bullets)
    {
        return new BacklogItem
        {
            Level = BacklogLevel.Issue,
            Title = title,
            Code = code,
            Bullets = bullets
        };
    }

    private static BoardWorkItem Work(
        int id,
        string type,
        string title,
        int? parentId = null,
        string state = "New",
        string description = "",
        string iterationPath = "")
    {
        return new BoardWorkItem
        {
            Id = id,
            Title = title,
            WorkItemType = type,
            ParentId = parentId,
            State = state,
            Description = description,
            IterationPath = iterationPath
        };
    }

    // ------------------------------------------------------------------ sync

    [Fact]
    public void SyncPlansTheChainInTheDocumentedOrder()
    {
        var snapshot = BoardSnapshot.From([
            Work(1, "Issue", "PROJ-101 · Drifted title"),
            Work(2, "Task", "An orphaned task", 1)
        ]);

        var plan = PlanBuilder.BuildSync(
            Config(),
            [
                IssueWithBullets("PROJ-101", "PROJ-101 · Fresh title", "write the tests"),
                Issue("PROJ-102", "PROJ-102 · Missing")
            ],
            snapshot,
            Markdown).Value;

        Assert.Equal(PlanCommand.Sync, plan.Command);
        Assert.Equal(2, plan.CreateCount); // PROJ-102 (import) and the missing Task (resync-tasks)
        Assert.Equal(1, plan.UpdateCount); // PROJ-101's drifted title (resync)
        Assert.Equal(1, plan.DeleteCount); // the stray Task (resync-tasks)

        // Import's rows first, then resync's, then the task reconcile's: the
        // PROJ-102 create (import) precedes the PROJ-101 update (resync).
        var withIndex = plan.Rows.Select((r, i) => (r, i)).ToArray();
        var create = withIndex.First(x => x.r.Code == "PROJ-102" && x.r.IsCreate);
        var update = withIndex.First(x => x.r.IsUpdate);
        Assert.True(create.i < update.i);
        Assert.Contains(plan.Notes, n => n.Contains("import, then resync"));
        Assert.True(plan.HasWork);
    }

    // BuildSync refuses to plan while BacklogMarkupAudit reports problems — the
    // CLI's check-html abort (FSD §3.3.4). There is deliberately no authored-input
    // test for it here: both converters escape raw angle brackets, so no typed
    // description can produce unbalanced HTML (the PRD-AC-03 decision
    // `markup-gate-unreachable-from-the-editor`), and AcceptanceTests already
    // drives the refusal through the workspace it can build by hand.

    [Fact]
    public void SyncIsQuietWhenTheBoardAlreadyMatches()
    {
        var snapshot = BoardSnapshot.From([
            Work(1, "Issue", "PROJ-101 · A"),
            Work(2, "Task", "write the tests", 1)
        ]);

        var built = PlanBuilder.BuildSync(
            Config(), [IssueWithBullets("PROJ-101", "PROJ-101 · A", "write the tests")], snapshot, Markdown);

        Assert.False(built.Value.HasWork);
        Assert.Equal("the board already matches the backlog", built.Value.Summary);
    }

    // ------------------------------------------------------------- set-state

    [Fact]
    public void SetStateMovesNamedItemsToTheConfiguredTerminalState()
    {
        var snapshot = BoardSnapshot.From([
            Work(5, "Issue", "PROJ-101 · A", state: "To Do"),
            Work(6, "Issue", "PROJ-102 · B", state: "Done")
        ]);

        var plan = PlanBuilder.BuildSetState(
            Config(), snapshot, Markdown, [5, 6]);

        var row = plan.Rows.Single(r => r.IsUpdate);
        Assert.Equal(5, row.BoardId);
        Assert.Equal("Done", row.Changes[0].After);
        Assert.Equal(1, plan.UnchangedCount); // #6 is already Done
        Assert.Equal(PlanCommand.SetState, plan.Command);
    }

    [Fact]
    public void SetStateTicksALeadingOpenCheckboxOnTheWayToDone()
    {
        var snapshot = BoardSnapshot.From([
            Work(5, "Task", "[ ] finish the report", state: "New")
        ]);

        var plan = PlanBuilder.BuildSetState(Config(), snapshot, Markdown, [5]);

        var row = plan.Rows.Single();
        Assert.Equal(2, row.Changes.Count());
        Assert.Equal("[x] finish the report", row.Changes.Single(c => c.Field == BoardFieldChange.TitleField).After);
    }

    [Fact]
    public void SetStateWithNoTickLeavesTheTitleAlone()
    {
        var snapshot = BoardSnapshot.From([
            Work(5, "Task", "[ ] finish the report", state: "New")
        ]);

        var plan = PlanBuilder.BuildSetState(Config(), snapshot, Markdown, [5], noTick: true);

        var row = plan.Rows.Single();
        var change = Assert.Single(row.Changes);
        Assert.Equal(BoardFieldChange.StateField, change.Field);
    }

    [Fact]
    public void SetStateAcceptsAnExplicitTargetStateAndThenNeverTicks()
    {
        var snapshot = BoardSnapshot.From([
            Work(5, "Task", "[ ] finish the report", state: "New")
        ]);

        var plan = PlanBuilder.BuildSetState(Config(), snapshot, Markdown, [5], "Removed");

        Assert.Equal("Removed", plan.Rows.Single().Changes[0].After);
        Assert.Single(plan.Rows.Single().Changes);
    }

    [Fact]
    public void SetStateReportsIdsTheBoardDoesNotHoldAsNotes()
    {
        var snapshot = BoardSnapshot.From([Work(5, "Issue", "PROJ-101 · A", state: "To Do")]);

        var plan = PlanBuilder.BuildSetState(Config(), snapshot, Markdown, [5, 99]);

        Assert.Contains(plan.Notes, n => n.Contains("#99"));
        Assert.Equal(1, plan.UpdateCount);
    }

    [Fact]
    public void AdvanceMovesIssuesWithCommitEvidenceToTheWorkingState()
    {
        var snapshot = BoardSnapshot.From([
            Work(1, "Issue", "PROJ-101 · A", state: "To Do")
        ]);
        var probe = new GitProbeReport(
            [
                new GitBranchEvidence("/repo", "feature/DDI-101", "PROJ-101", 3),
                new GitBranchEvidence("/repo", "bugfix/PROJ-101-fix", "PROJ-101", 1)
            ],
            []);

        var built = PlanBuilder.BuildAdvance(
            Config(TodoDoing), [Issue("PROJ-101", "PROJ-101 · A")],
            snapshot, Markdown, probe, "origin/main");

        var plan = built.Value;
        Assert.Equal(PlanCommand.Advance, plan.Command);
        var row = plan.Rows.Single();
        Assert.Equal(PlanOperation.Update, row.Operation);
        Assert.Equal("Doing", row.Changes[0].After);
        Assert.Equal("feature/DDI-101 (+3); bugfix/PROJ-101-fix (+1)", row.Detail);
    }

    [Fact]
    public void AdvanceLeavesAnIssuePastItsStartStateUnchanged()
    {
        var snapshot = BoardSnapshot.From([
            Work(1, "Issue", "PROJ-101 · A", state: "Doing")
        ]);
        var probe = new GitProbeReport([new GitBranchEvidence("/r", "feature/PROJ-101", "PROJ-101", 2)], []);

        var built = PlanBuilder.BuildAdvance(
            Config(TodoDoing), [Issue("PROJ-101", "PROJ-101 · A")], snapshot, Markdown, probe, "origin/main");

        Assert.Equal(PlanOperation.Unchanged, built.Value.Rows.Single().Operation);
        Assert.False(built.Value.HasWork);
    }

    [Fact]
    public void AdvanceNeverPlansATerminalState()
    {
        var snapshot = BoardSnapshot.From([
            Work(1, "Issue", "PROJ-101 · A", state: "To Do")
        ]);
        var probe = new GitProbeReport([new GitBranchEvidence("/r", "feature/PROJ-101", "PROJ-101", 7)], []);

        var built = PlanBuilder.BuildAdvance(
            Config(TodoDoing), [Issue("PROJ-101", "PROJ-101 · A")], snapshot, Markdown, probe, "origin/main");

        Assert.All(built.Value.WriteRows, r =>
            Assert.All(r.Changes, c => Assert.NotEqual("Done", c.After)));
    }

    [Fact]
    public void AdvanceReportsABacklogIssueWithEvidenceButNoBoardItem()
    {
        var snapshot = BoardSnapshot.From([
            Work(1, "Issue", "PROJ-101 · A", state: "To Do")
        ]);
        var probe = new GitProbeReport([new GitBranchEvidence("/r", "feature/PROJ-102", "PROJ-102", 1)], []);

        var built = PlanBuilder.BuildAdvance(
            Config(TodoDoing), [Issue("PROJ-101", "PROJ-101 · A"), Issue("PROJ-102", "PROJ-102 · B")],
            snapshot, Markdown, probe, "origin/main");

        // PROJ-102 has evidence and a backlog entry but no board Issue: the CLI
        // calls it a ghost and says so instead of planning a write.
        Assert.Empty(built.Value.Rows);
        Assert.Contains(built.Value.Notes, n => n.Contains("PROJ-102"));
    }

    [Fact]
    public void AdvanceRefusesToPlanWithoutConfiguredStartAndWorkingStates()
    {
        var snapshot = BoardSnapshot.From([Work(1, "Issue", "PROJ-101 · A", state: "New")]);

        var built = PlanBuilder.BuildAdvance(
            Config(), [Issue("PROJ-101", "PROJ-101 · A")], snapshot, Markdown,
            GitProbeReport.Empty, "origin/main");

        Assert.True(built.IsFailure);
        Assert.Equal("states.not_configured", built.Error!.Code);
    }

    [Fact]
    public void SprintPlansCarryTheResetOnMissingOptionTheyWereBuiltWith()
    {
        var snapshot = BoardSnapshot.From([Work(1, "Issue", "PROJ-101 · A", iterationPath: @"p\Old")]);

        Assert.True(PlanBuilder
            .BuildSprints(Config(OneSprint), snapshot, Markdown, resetOnMissing: true)
            .ResetOnMissing);
        Assert.False(PlanBuilder.BuildSprints(Config(OneSprint), snapshot, Markdown).ResetOnMissing);
    }

    [Fact]
    public async Task ApplyResetsAFailedIterationWriteToTheProjectRootWhenThePlanSaysTo()
    {
        var gateway = new FakeBoardGateway();
        var id = gateway.Seed("Issue", "PROJ-101 · A", iterationPath: @"p\Old");
        var snapshot = BoardSnapshot.From([.. gateway.Items]);
        var plan = PlanBuilder.BuildSprints(
            Config(OneSprint), snapshot, Markdown, resetOnMissing: true);
        gateway.FailNextUpdate = Error.SourceFailure("board.iteration_missing", "iteration not found");

        var report = await ApplyExecutor.ApplyAsync(
            gateway, Config(OneSprint), plan, PlanBuilder.FingerprintBacklog(Markdown), snapshot.Fingerprint);

        Assert.True(report.IsSuccess);
        var outcome = Assert.Single(report.Value.Outcomes, o => o.Row.Target == PlanTarget.WorkItem);
        Assert.False(outcome.Succeeded); // the CLI counts a failed assignment as failed
        Assert.Contains("reset to the project root", outcome.Message);

        // The failed write was never applied; the recovery was, and it moved the
        // item to the project root.
        var reset = Assert.Single(gateway.Updated);
        Assert.Equal(id, reset.Id);
        Assert.Equal("p", reset.Changes[0].Value);
        Assert.Equal("p", gateway.Items.Single(i => i.Id == id).IterationPath);
    }

    [Fact]
    public async Task ApplyLeavesAFailedIterationWriteAloneWithoutTheOption()
    {
        var gateway = new FakeBoardGateway();
        var id = gateway.Seed("Issue", "PROJ-101 · A", iterationPath: @"p\Old");
        var snapshot = BoardSnapshot.From([.. gateway.Items]);
        var plan = PlanBuilder.BuildSprints(Config(OneSprint), snapshot, Markdown);
        gateway.FailNextUpdate = Error.SourceFailure("board.iteration_missing", "iteration not found");

        var report = await ApplyExecutor.ApplyAsync(
            gateway, Config(OneSprint), plan, PlanBuilder.FingerprintBacklog(Markdown), snapshot.Fingerprint);

        Assert.True(report.IsSuccess);
        var outcome = Assert.Single(report.Value.Outcomes, o => o.Row.Target == PlanTarget.WorkItem);
        Assert.False(outcome.Succeeded);
        Assert.DoesNotContain("reset", outcome.Message);
        Assert.Empty(gateway.Updated); // failed write, and no recovery without the option
        Assert.Equal(@"p\Old", gateway.Items.Single(i => i.Id == id).IterationPath);
    }
}