using AdoBoardSync.Core.Backlog;
using AdoBoardSync.Core.Board;
using AdoBoardSync.Core.Configuration;
using AdoBoardSync.Core.Markdown;
using AdoBoardSync.Core.Planning;

namespace AdoBoardSync.Desktop.Tests;

/// <summary>
/// Pins <c>audit</c>: the read-only check that the board still matches the
/// backlog. The gate this suite protects is that a board the CLI exits 1 on is a
/// board this reports as not clean — check for check.
/// </summary>
public class PlanBuilderAuditTests
{
    private const string Markdown = "## Epic 1\n\n### PROJ-101 · A\n";

    private static BoardConfig Config() =>
        BoardConfig.Parse(
            """{"org":"o","project":"p","code_prefix":"PROJ","board_file":"backlog.md"}""",
            Path.GetTempPath()).Value;

    private static BoardWorkItem Work(
        int id,
        string type,
        string title,
        int? parentId = null,
        string state = "New",
        string description = "") => new()
    {
        Id = id,
        Title = title,
        WorkItemType = type,
        ParentId = parentId,
        State = state,
        Description = description,
    };

    private static BacklogItem Epic(string title) => new()
    {
        Level = BacklogLevel.Epic,
        Title = title,
    };

    private static BacklogItem Issue(string code, string title, string[]? body = null, string[]? bullets = null) => new()
    {
        Level = BacklogLevel.Issue,
        Title = title,
        Code = code,
        DescriptionLines = body ?? [],
        Bullets = bullets ?? [],
    };

    private static string Html(params string[] lines) => MarkdownHtml.ToHtml(lines);

    [Fact]
    public void ABoardThatMatchesTheBacklogIsClean()
    {
        var snapshot = BoardSnapshot.From([
            Work(1, "Epic", "Epic 1"),
            Work(2, "Issue", "PROJ-101 · A", parentId: 1, description: Html("Body.")),
        ]);

        var report = PlanBuilder.BuildAudit(
            Config(), [Epic("Epic 1"), Issue("PROJ-101", "PROJ-101 · A", ["Body."])], snapshot, Markdown);

        Assert.True(report.IsClean);
        Assert.Equal("The board matches the backlog.", report.Summary);
        Assert.Equal(1, report.BoardEpicCount);
        Assert.Equal(1, report.BacklogEpicCount);
    }

    [Fact]
    public void AnIssueInTheBacklogAndNotOnTheBoardIsMissing()
    {
        var snapshot = BoardSnapshot.From([Work(1, "Epic", "Epic 1")]);

        var report = PlanBuilder.BuildAudit(
            Config(), [Epic("Epic 1"), Issue("PROJ-101", "PROJ-101 · A")], snapshot, Markdown);

        var finding = Assert.Single(report.Findings, f => f.Kind == AuditKind.Missing);
        Assert.Equal("PROJ-101", finding.Code);
    }

    [Fact]
    public void AnIssueOnTheBoardAndNotInTheBacklogIsExtra()
    {
        var snapshot = BoardSnapshot.From([
            Work(1, "Epic", "Epic 1"),
            Work(2, "Issue", "PROJ-999 · Ghost", parentId: 1),
        ]);

        var report = PlanBuilder.BuildAudit(Config(), [Epic("Epic 1")], snapshot, Markdown);

        var finding = Assert.Single(report.Findings, f => f.Kind == AuditKind.Extra);
        Assert.Equal("PROJ-999", finding.Code);
        Assert.Equal(2, finding.BoardId);
    }

    [Fact]
    public void TwoWorkItemsCarryingOneCodeAreADuplicate()
    {
        var snapshot = BoardSnapshot.From([
            Work(1, "Epic", "Epic 1"),
            Work(4, "Issue", "PROJ-101 · A", parentId: 1),
            Work(9, "Issue", "PROJ-101 · A again", parentId: 1),
        ]);

        var report = PlanBuilder.BuildAudit(
            Config(), [Epic("Epic 1"), Issue("PROJ-101", "PROJ-101 · A")], snapshot, Markdown);

        var finding = Assert.Single(report.Findings, f => f.Kind == AuditKind.Duplicate);
        Assert.Equal([4, 9], finding.BoardIds);
        Assert.Contains("#4", finding.Detail);
    }

    [Fact]
    public void ATaskCitingAnIssueCodeIsNeitherDuplicateNorDrift()
    {
        // The defect this pins was found on a real board: a Task titled
        // "…surfaced to monitoring (PROJ-101)" was sorted into the Issue bucket,
        // inventing both a phantom duplicate and a phantom description drift.
        var snapshot = BoardSnapshot.From([
            Work(1, "Epic", "Epic 1"),
            Work(2, "Issue", "PROJ-101 · A", parentId: 1, description: Html("Body.")),
            Work(3, "Task", "Rejections are logged (PROJ-101)", parentId: 2),
        ]);

        var report = PlanBuilder.BuildAudit(
            Config(),
            [Epic("Epic 1"), Issue("PROJ-101", "PROJ-101 · A", ["Body."], ["Rejections are logged (PROJ-101)"])],
            snapshot,
            Markdown);

        Assert.DoesNotContain(report.Findings, f => f.Kind == AuditKind.Duplicate);
        Assert.DoesNotContain(report.Findings, f => f.Kind == AuditKind.DescriptionDrift);
        Assert.True(report.IsClean);
    }

    [Fact]
    public void ATitleOrDescriptionThatDivergedIsDrift()
    {
        var snapshot = BoardSnapshot.From([
            Work(1, "Epic", "Epic 1"),
            Work(2, "Issue", "PROJ-101 · Old title", parentId: 1, description: Html("Old body.")),
        ]);

        var report = PlanBuilder.BuildAudit(
            Config(), [Epic("Epic 1"), Issue("PROJ-101", "PROJ-101 · New title", ["New body."])], snapshot, Markdown);

        Assert.Contains(report.Findings, f => f.Kind == AuditKind.TitleDrift);
        Assert.Contains(report.Findings, f => f.Kind == AuditKind.DescriptionDrift);
    }

    [Fact]
    public void ADescriptionTheBoardMerelyReformattedIsNotDrift()
    {
        // Compared normalised, so an HTML artefact the board added itself is not
        // rewritten on every run.
        var snapshot = BoardSnapshot.From([
            Work(1, "Epic", "Epic 1"),
            Work(2, "Issue", "PROJ-101 · A", parentId: 1, description: "  " + Html("Body.") + "\n"),
        ]);

        var report = PlanBuilder.BuildAudit(
            Config(), [Epic("Epic 1"), Issue("PROJ-101", "PROJ-101 · A", ["Body."])], snapshot, Markdown);

        Assert.DoesNotContain(report.Findings, f => f.Kind == AuditKind.DescriptionDrift);
    }

    [Fact]
    public void BulletsAndChildTasksAreComparedBothWays()
    {
        var snapshot = BoardSnapshot.From([
            Work(1, "Epic", "Epic 1"),
            Work(2, "Issue", "PROJ-101 · A", parentId: 1, description: Html("Body.")),
            Work(3, "Task", "Stray work", parentId: 2),
        ]);

        var report = PlanBuilder.BuildAudit(
            Config(),
            [Epic("Epic 1"), Issue("PROJ-101", "PROJ-101 · A", ["Body."], ["Wanted work"])],
            snapshot,
            Markdown);

        Assert.Single(report.Findings, f => f.Kind == AuditKind.MissingTask && f.Title == "Wanted work");
        Assert.Single(report.Findings, f => f.Kind == AuditKind.StrayTask && f.Title == "Stray work");
        Assert.Equal(1, report.IssuesTaskChecked);
    }

    [Fact]
    public void ADoneParentWithOpenDescendantsIsReportedWithEveryOneOfThem()
    {
        var snapshot = BoardSnapshot.From([
            Work(1, "Epic", "Epic 1", state: "Done"),
            Work(2, "Issue", "PROJ-101 · A", parentId: 1, state: "Active", description: Html("Body.")),
            Work(3, "Task", "Wanted work", parentId: 2, state: "New"),
        ]);

        var report = PlanBuilder.BuildAudit(
            Config(),
            [Epic("Epic 1"), Issue("PROJ-101", "PROJ-101 · A", ["Body."], ["Wanted work"])],
            snapshot,
            Markdown);

        var finding = Assert.Single(report.OpenDescendantsOfDone);
        Assert.Equal(1, finding.BoardId);
        Assert.Equal([2, 3], finding.BoardIds);
    }

    [Fact]
    public void AParentWhoseChildrenAreAllDoneIsForReviewNotFailure()
    {
        // The CLI prints this but does not exit 1 on it: a parent can hold sign-off
        // work of its own, so it is a judgement call rather than drift.
        var snapshot = BoardSnapshot.From([
            Work(1, "Epic", "Epic 1", state: "Active"),
            Work(2, "Issue", "PROJ-101 · A", parentId: 1, state: "Done", description: Html("Body.")),
        ]);

        var report = PlanBuilder.BuildAudit(
            Config(), [Epic("Epic 1"), Issue("PROJ-101", "PROJ-101 · A", ["Body."])], snapshot, Markdown);

        Assert.True(report.IsClean);
        Assert.Equal(1, Assert.Single(report.Reviews).BoardId);
    }

    [Fact]
    public void AnEpicCountThatDisagreesIsItsOwnFinding()
    {
        var snapshot = BoardSnapshot.From([
            Work(1, "Epic", "Epic 1"),
            Work(2, "Epic", "Another epic entirely"),
        ]);

        var report = PlanBuilder.BuildAudit(Config(), [Epic("Epic 1")], snapshot, Markdown);

        Assert.Contains(report.Findings, f => f.Kind == AuditKind.CountMismatch);
        Assert.Equal(2, report.BoardEpicCount);
        Assert.Equal(1, report.BacklogEpicCount);
    }

    [Fact]
    public void AnEpicWhoseBoardTitleIsAPrefixOfTheBacklogsStillMatches()
    {
        // Import matches an Epic by substring in both directions. Audit has to
        // agree with import, or it reports drift import would never fix.
        var snapshot = BoardSnapshot.From([Work(1, "Epic", "Epic 1")]);

        var report = PlanBuilder.BuildAudit(
            Config(), [Epic("Epic 1: product foundation")], snapshot, Markdown);

        Assert.DoesNotContain(report.Findings, f => f.Kind == AuditKind.Missing);
        Assert.DoesNotContain(report.Findings, f => f.Kind == AuditKind.Extra);
    }

    [Fact]
    public void TheReportCarriesTheFingerprintsItWasComputedAgainst()
    {
        var snapshot = BoardSnapshot.From([Work(1, "Epic", "Epic 1")]);

        var report = PlanBuilder.BuildAudit(Config(), [Epic("Epic 1")], snapshot, Markdown);

        Assert.Equal(snapshot.Fingerprint, report.BoardFingerprint);
        Assert.Equal(PlanBuilder.FingerprintBacklog(Markdown), report.BacklogFingerprint);
    }
}
