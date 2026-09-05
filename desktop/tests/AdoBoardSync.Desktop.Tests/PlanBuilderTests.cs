using AdoBoardSync.Core.Backlog;
using AdoBoardSync.Core.Board;
using AdoBoardSync.Core.Configuration;
using AdoBoardSync.Core.Markdown;
using AdoBoardSync.Core.Planning;

namespace AdoBoardSync.Desktop.Tests;

/// <summary>
/// Pins the rules ported from the CLI's <c>import</c> and <c>resync</c>: what the
/// Plan says is what Apply does.
/// </summary>
public class PlanBuilderTests
{
    private const string Markdown = "## Epic 1\n\n### PROJ-101 · A\n";

    internal static BoardConfig Config()
    {
        var parsed = BoardConfig.Parse(
            """{"org":"o","project":"p","code_prefix":"PROJ","board_file":"backlog.md"}""",
            Path.GetTempPath());

        return parsed.Value;
    }

    private static BacklogItem Epic(string title, params string[] description) => new()
    {
        Level = BacklogLevel.Epic,
        Title = title,
        DescriptionLines = description,
    };

    private static BacklogItem Issue(string code, string title, params string[] description) => new()
    {
        Level = BacklogLevel.Issue,
        Title = title,
        Code = code,
        DescriptionLines = description,
    };

    private static BacklogItem TaskIssue(string code, string title, params string[] bullets) => new()
    {
        Level = BacklogLevel.Issue,
        Title = title,
        Code = code,
        Bullets = bullets,
    };

    private static BoardWorkItem OnBoard(int id, string type, string title, string description = "", int? parentId = null) => new()
    {
        Id = id,
        Title = title,
        WorkItemType = type,
        Description = description,
        ParentId = parentId,
    };

    // ---------------------------------------------------------------- import

    [Fact]
    public void ImportPlansOnlyWhatTheBoardIsMissing()
    {
        var snapshot = BoardSnapshot.From([OnBoard(1, "Issue", "PROJ-101 · Already there")]);

        var plan = PlanBuilder.BuildImport(
            Config(),
            [Epic("Epic 1"), Issue("PROJ-101", "PROJ-101 · Already there"), Issue("PROJ-102", "PROJ-102 · New")],
            snapshot,
            Markdown);

        Assert.Equal(PlanCommand.Import, plan.Command);
        Assert.Equal(2, plan.CreateCount);   // the Epic, and PROJ-102
        Assert.Equal(1, plan.UnchangedCount);
        Assert.Contains(plan.Rows, r => r.Code == "PROJ-102" && r.Operation == PlanOperation.Create);
        Assert.Contains(plan.Rows, r => r.Code == "PROJ-101" && r.Operation == PlanOperation.Unchanged);
    }

    [Fact]
    public void ImportNeverUpdatesAnItemThatAlreadyExists()
    {
        // Same code, completely different title and description on the board.
        var snapshot = BoardSnapshot.From([OnBoard(1, "Issue", "PROJ-101 · Stale title", "<p>stale</p>")]);

        var plan = PlanBuilder.BuildImport(
            Config(), [Issue("PROJ-101", "PROJ-101 · Fresh title", "fresh")], snapshot, Markdown);

        Assert.Equal(0, plan.CreateCount);
        Assert.Equal(0, plan.UpdateCount);
        Assert.False(plan.HasWork);
    }

    [Fact]
    public void ANewIssueIsParentedToAnEpicTheSamePlanCreates()
    {
        var plan = PlanBuilder.BuildImport(
            Config(),
            [Epic("Epic 1"), Issue("PROJ-101", "PROJ-101 · A")],
            BoardSnapshot.From([]),
            Markdown);

        var issue = Assert.Single(plan.Rows, r => r.Code == "PROJ-101");
        Assert.Null(issue.ParentBoardId);
        Assert.Equal("Epic 1", issue.ParentTitle);
    }

    [Fact]
    public void ANewIssueIsParentedToAnEpicTheBoardAlreadyHas()
    {
        var snapshot = BoardSnapshot.From([OnBoard(7, "Epic", "Epic 1")]);

        var plan = PlanBuilder.BuildImport(
            Config(), [Epic("Epic 1"), Issue("PROJ-101", "PROJ-101 · A")], snapshot, Markdown);

        var issue = Assert.Single(plan.Rows, r => r.Code == "PROJ-101");
        Assert.Equal(7, issue.ParentBoardId);
        Assert.Null(issue.ParentTitle);
    }

    [Fact]
    public void AnEpicMatchesLooselySoARenameDoesNotCreateASecondOne()
    {
        // The CLI matches an Epic by substring in either direction.
        var snapshot = BoardSnapshot.From([OnBoard(7, "Epic", "Epic 1 — Platform")]);

        var plan = PlanBuilder.BuildImport(
            Config(), [Epic("Epic 1 — Platform Foundations")], snapshot, Markdown);

        Assert.Equal(0, plan.CreateCount);
        Assert.Equal(7, Assert.Single(plan.Rows).BoardId);
    }

    [Fact]
    public void TwoBacklogRowsCarryingOneCodeCreateOneItem()
    {
        var plan = PlanBuilder.BuildImport(
            Config(),
            [Epic("Epic 1"), Issue("PROJ-101", "PROJ-101 · A"), Issue("PROJ-101", "PROJ-101 · duplicated")],
            BoardSnapshot.From([]),
            Markdown);

        Assert.Single(plan.Rows, r => r.Code == "PROJ-101");
    }

    // ---------------------------------------------------------------- resync

    [Fact]
    public void ResyncPlansATitleAndDescriptionUpdate()
    {
        var snapshot = BoardSnapshot.From(
            [OnBoard(5, "Issue", "PROJ-101 · Old title", "<p>old</p>")]);

        var plan = PlanBuilder.BuildResync(
            Config(), [Issue("PROJ-101", "PROJ-101 · New title", "new")], snapshot, Markdown);

        var row = Assert.Single(plan.Rows);
        Assert.Equal(PlanOperation.Update, row.Operation);
        Assert.Equal(5, row.BoardId);
        Assert.Equal(2, row.Changes.Count);
        Assert.Contains(row.Changes, c => c.Field == BoardFieldChange.TitleField && c.After == "PROJ-101 · New title");
        Assert.Contains(row.Changes, c => c.Field == BoardFieldChange.DescriptionField);
    }

    [Fact]
    public void ResyncLeavesAnItemAloneWhenOnlyTheHtmlShapeDiffers()
    {
        // The board stores what import sent; comparing normalised text stops a
        // cosmetic difference being rewritten on every single run.
        var html = MarkdownHtml.ToHtml(["identical text"]);
        var snapshot = BoardSnapshot.From([OnBoard(5, "Issue", "PROJ-101 · A", html + "\n")]);

        var plan = PlanBuilder.BuildResync(
            Config(), [Issue("PROJ-101", "PROJ-101 · A", "identical text")], snapshot, Markdown);

        Assert.Equal(PlanOperation.Unchanged, Assert.Single(plan.Rows).Operation);
        Assert.False(plan.HasWork);
    }

    [Fact]
    public void ResyncNeverCreatesAnythingForABacklogItemTheBoardLacks()
    {
        var plan = PlanBuilder.BuildResync(
            Config(), [Issue("PROJ-999", "PROJ-999 · Not on the board")], BoardSnapshot.From([]), Markdown);

        Assert.Empty(plan.Rows);
        Assert.Equal(0, plan.CreateCount);
    }

    [Fact]
    public void ABoardItemWithNoMatchingBacklogCodeIsLeftAlone()
    {
        var snapshot = BoardSnapshot.From([OnBoard(5, "Issue", "PROJ-500 · Only on the board")]);

        var plan = PlanBuilder.BuildResync(
            Config(), [Issue("PROJ-101", "PROJ-101 · A")], snapshot, Markdown);

        Assert.Empty(plan.Rows);
    }

    // ---------------------------------------------------------- resync-tasks

    private static BoardWorkItem TaskOnBoard(int id, int parentId, string title) =>
        OnBoard(id, "Task", title, parentId: parentId);

    [Fact]
    public void ResyncTasksCreatesTheMissingTaskAndParentsItToTheBoardIssue()
    {
        var snapshot = BoardSnapshot.From([OnBoard(7, "Issue", "PROJ-101 · A")]);

        // Bullets reach this builder exactly as the backlog parser emits them:
        // the leading '- ' marker is already stripped.
        var plan = PlanBuilder.BuildResyncTasks(
            Config(),
            [TaskIssue("PROJ-101", "PROJ-101 · A", "Implement the append-only `EventStore`")],
            snapshot,
            Markdown);

        var row = Assert.Single(plan.WriteRows);
        Assert.Equal(PlanOperation.Create, row.Operation);
        Assert.Equal("Implement the append-only EventStore", row.Title);
        Assert.Equal("PROJ-101", row.Code);
        Assert.Equal(7, row.ParentBoardId);
        Assert.Equal(MarkdownHtml.Inline("Implement the append-only `EventStore`"), row.DescriptionHtml);
    }

    [Fact]
    public void ResyncTasksDeletesATaskWhoseBulletLeftTheBacklog()
    {
        var snapshot = BoardSnapshot.From([
            OnBoard(7, "Issue", "PROJ-101 · A"),
            TaskOnBoard(8, 7, "Add optimistic-concurrency checks"),
        ]);

        // The backlog bullet replaces the stray task entirely: it plans both the
        // missing bullet's create and the orphaned board task's delete.
        var plan = PlanBuilder.BuildResyncTasks(
            Config(), [TaskIssue("PROJ-101", "PROJ-101 · A", "Implement the store")], snapshot, Markdown);

        var deletion = Assert.Single(plan.Rows, r => r.IsDelete);
        Assert.Equal(8, deletion.BoardId);
        Assert.Equal(2, plan.WriteRows.Count);
        Assert.Contains(plan.Rows, r => r.IsCreate);
    }

    [Fact]
    public void ResyncTasksComparesTruncatedPlainTitlesTheWayTheCliDoes()
    {
        // The wanted key is the plain text cut at task_title_max (default 250):
        // an existing Task whose stored title equals that cut survives untouched,
        // while one whose stored title kept running past the cut matches nothing
        // and is replaced — the CLI's exact trade, ported whole.
        var bullet = string.Concat(Enumerable.Repeat("alpha beta gamma ", 20));
        var plain = MarkdownHtml.Plain(bullet);
        Assert.True(plain.Length > 250);

        var cutMatches = BoardSnapshot.From([
            OnBoard(7, "Issue", "PROJ-101 · A"),
            TaskOnBoard(8, 7, plain[..250]),
        ]);
        var inStep = PlanBuilder.BuildResyncTasks(
            Config(), [TaskIssue("PROJ-101", "PROJ-101 · A", bullet)], cutMatches, Markdown);
        Assert.False(inStep.HasWork);

        var overlongOnBoard = BoardSnapshot.From([
            OnBoard(7, "Issue", "PROJ-101 · A"),
            TaskOnBoard(8, 7, plain),
        ]);
        var replaceIt = PlanBuilder.BuildResyncTasks(
            Config(), [TaskIssue("PROJ-101", "PROJ-101 · A", bullet)], overlongOnBoard, Markdown);

        Assert.Equal(PlanOperation.Create, replaceIt.WriteRows[0].Operation);
        Assert.Equal(plain[..250], replaceIt.WriteRows[0].Title);
        Assert.Equal(8, Assert.Single(replaceIt.Rows, r => r.IsDelete).BoardId);
    }

    [Fact]
    public void ResyncTasksKeepsLastDuplicateBulletButOnlyPlansOneTask()
    {
        var snapshot = BoardSnapshot.From([OnBoard(7, "Issue", "PROJ-101 · A")]);

        var plan = PlanBuilder.BuildResyncTasks(
            Config(),
            [TaskIssue("PROJ-101", "PROJ-101 · A", "Same wording", "**Same** wording")],
            snapshot,
            Markdown);

        // Same plain key after stripping; the last bullet wins as the description
        // source, exactly like the CLI's dict build.
        var row = Assert.Single(plan.WriteRows);
        Assert.Equal(MarkdownHtml.Inline("**Same** wording"), row.DescriptionHtml);
    }

    [Fact]
    public void ResyncTasksSkipsABacklogCodeThatHasNoBoardItem()
    {
        var plan = PlanBuilder.BuildResyncTasks(
            Config(), [TaskIssue("PROJ-999", "PROJ-999 · Nowhere", "a task")],
            BoardSnapshot.From([]), Markdown);

        Assert.Empty(plan.Rows);
    }

    [Fact]
    public void ResyncTasksIgnoresTasksParentedOutsideItsIssues()
    {
        var snapshot = BoardSnapshot.From([
            OnBoard(7, "Issue", "PROJ-101 · A"),
            OnBoard(9, "Epic", "Epic 1"),
            TaskOnBoard(10, 9, "Hanging off the Epic, not any Issue"),
        ]);

        var plan = PlanBuilder.BuildResyncTasks(
            Config(), [TaskIssue("PROJ-101", "PROJ-101 · A", "real work")], snapshot, Markdown);

        var create = Assert.Single(plan.WriteRows);
        Assert.Equal(PlanOperation.Create, create.Operation);
        Assert.DoesNotContain(plan.Rows, r => r.BoardId == 10);
    }

    [Fact]
    public void ResyncTasksSummaryNamesBothKindsOfChange()
    {
        var snapshot = BoardSnapshot.From([
            OnBoard(7, "Issue", "PROJ-101 · A"),
            TaskOnBoard(8, 7, "stray"),
        ]);

        var plan = PlanBuilder.BuildResyncTasks(
            Config(), [TaskIssue("PROJ-101", "PROJ-101 · A", "replacement")], snapshot, Markdown);

        Assert.Equal("1 task(s) to create, 1 to delete", plan.Summary);
        Assert.Contains(plan.Rows, r => r.Glyph == "−");
    }


    [Fact]
    public void APlanRecordsTheBacklogAndBoardItWasComputedAgainst()
    {
        var snapshot = BoardSnapshot.From([OnBoard(1, "Issue", "PROJ-101 · A")]);

        var plan = PlanBuilder.BuildImport(Config(), [], snapshot, Markdown);

        Assert.Equal(snapshot.Fingerprint, plan.BoardFingerprint);
        Assert.Equal(PlanBuilder.FingerprintBacklog(Markdown), plan.BacklogFingerprint);
    }

    [Fact]
    public void ChangingATitleOnTheBoardChangesItsFingerprint()
    {
        var before = BoardSnapshot.From([OnBoard(1, "Issue", "PROJ-101 · A")]);
        var after = BoardSnapshot.From([OnBoard(1, "Issue", "PROJ-101 · A changed")]);

        Assert.NotEqual(before.Fingerprint, after.Fingerprint);
    }

    [Fact]
    public void WriteRowsExcludeEverythingUnchanged()
    {
        var snapshot = BoardSnapshot.From([OnBoard(1, "Issue", "PROJ-101 · A")]);

        var plan = PlanBuilder.BuildImport(
            Config(),
            [Issue("PROJ-101", "PROJ-101 · A"), Issue("PROJ-102", "PROJ-102 · B")],
            snapshot,
            Markdown);

        Assert.Single(plan.WriteRows);
        Assert.Equal("PROJ-102", plan.WriteRows[0].Code);
    }
}
