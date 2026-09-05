using AdoBoardSync.Core.Backlog;
using AdoBoardSync.Core.Board;
using AdoBoardSync.Core.Configuration;
using AdoBoardSync.Core.Markdown;
using AdoBoardSync.Core.Planning;
using AdoBoardSync.Desktop.Services;
using AdoBoardSync.Desktop.ViewModels;

namespace AdoBoardSync.Desktop.Tests;

/// <summary>
/// Pins the Audit surface (ABSD-304) and its close-children handoff (ABSD-306).
///
/// The load-bearing assertion in this file is negative: after an audit runs, the
/// fake board must have recorded no create, no update and no delete. A surface
/// that reports drift and can also correct it is one refactor away from
/// correcting it without a confirmation.
/// </summary>
public class AuditViewModelTests
{
    private const string Markdown = "## Epic 1\n\n### PROJ-101 · A\n";

    private static BoardConfig Config() =>
        BoardConfig.Parse(
            """{"org":"o","project":"p","code_prefix":"PROJ","board_file":"backlog.md"}""",
            Path.GetTempPath()).Value;

    private static BacklogWorkspace Workspace(params BacklogItem[] items) =>
        new(null, Config(), "backlog.md", Markdown, items, 0,
            FileStamp.For(DateTimeOffset.UnixEpoch, Markdown));

    private static BacklogItem Epic(string title) => new()
    {
        Level = BacklogLevel.Epic,
        Title = title,
    };

    private static BacklogItem Issue(string code, string title, params string[] body) => new()
    {
        Level = BacklogLevel.Issue,
        Title = title,
        Code = code,
        DescriptionLines = body,
    };

    private static BoardWorkItem Work(
        int id, string type, string title, int? parentId = null,
        string state = "New", string description = "") => new()
    {
        Id = id,
        Title = title,
        WorkItemType = type,
        ParentId = parentId,
        State = state,
        Description = description,
    };

    /// <summary>A view model wired to a fake board and a token that always resolves.</summary>
    private static (AuditViewModel Audit, FakeBoardGateway Board) Subject(params BoardWorkItem[] items)
    {
        var board = new FakeBoardGateway();
        foreach (var item in items)
        {
            board.Items.Add(item);
        }

        var audit = new AuditViewModel(_ => board, new UnavailableCredentialStore("test"))
        {
            SessionToken = "token-for-the-test",
        };

        return (audit, board);
    }

    [Fact]
    public async Task AuditingACleanBoardReportsItAsCleanAndWritesNothing()
    {
        var html = MarkdownHtml.ToHtml(["Body."]);
        var (audit, board) = Subject(
            Work(1, "Epic", "Epic 1"),
            Work(2, "Issue", "PROJ-101 · A", parentId: 1, description: html));

        await audit.RunAsync(Workspace(Epic("Epic 1"), Issue("PROJ-101", "PROJ-101 · A", "Body.")));

        Assert.True(audit.HasReport);
        Assert.True(audit.IsClean);
        Assert.Empty(audit.Findings);
        Assert.Equal("The board matches the backlog.", audit.StatusText);

        Assert.Empty(board.Created);
        Assert.Empty(board.Updated);
        Assert.Empty(board.Deleted);
    }

    [Fact]
    public async Task AuditingADriftedBoardListsEveryDifferenceAndStillWritesNothing()
    {
        var (audit, board) = Subject(
            Work(1, "Epic", "Epic 1"),
            Work(2, "Issue", "PROJ-101 · Old title", parentId: 1, description: "<div>Stale</div>"));

        await audit.RunAsync(Workspace(Epic("Epic 1"), Issue("PROJ-101", "PROJ-101 · New title", "Body.")));

        Assert.False(audit.IsClean);
        Assert.Contains(audit.Findings, f => f.Kind == AuditKind.TitleDrift);
        Assert.Contains(audit.Findings, f => f.Kind == AuditKind.DescriptionDrift);

        Assert.Empty(board.Created);
        Assert.Empty(board.Updated);
        Assert.Empty(board.Deleted);
    }

    [Fact]
    public async Task TheHeaderCountsAreShownEvenOnACleanBoard()
    {
        // "checked 1 issue against backlog bullets" is what makes a pass mean
        // something rather than looking like a no-op.
        var html = MarkdownHtml.ToHtml(["Body."]);
        var (audit, _) = Subject(
            Work(1, "Epic", "Epic 1"),
            Work(2, "Issue", "PROJ-101 · A", parentId: 1, description: html));

        await audit.RunAsync(Workspace(Epic("Epic 1"), Issue("PROJ-101", "PROJ-101 · A", "Body.")));

        Assert.Contains(audit.HeaderLines, line => line.StartsWith("Epics: board 1 / backlog 1", StringComparison.Ordinal));
        Assert.Contains(audit.HeaderLines, line => line.Contains("Duplicates: 0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AParentWhoseChildrenAreAllDoneIsListedForReviewNotAsDrift()
    {
        var html = MarkdownHtml.ToHtml(["Body."]);
        var (audit, _) = Subject(
            Work(1, "Epic", "Epic 1", state: "Active"),
            Work(2, "Issue", "PROJ-101 · A", parentId: 1, state: "Done", description: html));

        await audit.RunAsync(Workspace(Epic("Epic 1"), Issue("PROJ-101", "PROJ-101 · A", "Body.")));

        Assert.True(audit.IsClean);
        Assert.True(audit.HasReviews);
        Assert.Equal(1, Assert.Single(audit.Reviews).BoardId);
    }

    [Fact]
    public async Task TheCloseChildrenHandoffIsOfferedOnlyWhenItWouldDoSomething()
    {
        var html = MarkdownHtml.ToHtml(["Body."]);
        var (clean, _) = Subject(
            Work(1, "Epic", "Epic 1"),
            Work(2, "Issue", "PROJ-101 · A", parentId: 1, description: html));

        await clean.RunAsync(Workspace(Epic("Epic 1"), Issue("PROJ-101", "PROJ-101 · A", "Body.")));
        Assert.False(clean.CanCloseChildren);

        var (drifted, _) = Subject(
            Work(1, "Epic", "Epic 1", state: "Done"),
            Work(2, "Issue", "PROJ-101 · A", parentId: 1, state: "Active", description: html));

        await drifted.RunAsync(Workspace(Epic("Epic 1"), Issue("PROJ-101", "PROJ-101 · A", "Body.")));
        Assert.True(drifted.CanCloseChildren);
        Assert.Equal("Plan the close of 1 open descendant", drifted.CloseChildrenCaption);
    }

    [Fact]
    public async Task TheHandoffAsksTheShellAndNeverWritesByItself()
    {
        var html = MarkdownHtml.ToHtml(["Body."]);
        var (audit, board) = Subject(
            Work(1, "Epic", "Epic 1", state: "Done"),
            Work(2, "Issue", "PROJ-101 · A", parentId: 1, state: "Active", description: html));

        var asked = 0;
        audit.CloseChildrenRequested = () => asked++;

        await audit.RunAsync(Workspace(Epic("Epic 1"), Issue("PROJ-101", "PROJ-101 · A", "Body.")));
        audit.RequestCloseChildren();

        Assert.Equal(1, asked);
        Assert.Empty(board.Updated);
    }

    [Fact]
    public void TheHandoffDoesNothingWhenThereIsNoReport()
    {
        var (audit, _) = Subject();
        var asked = 0;
        audit.CloseChildrenRequested = () => asked++;

        audit.RequestCloseChildren();

        Assert.Equal(0, asked);
    }

    [Fact]
    public async Task AnAuditIsRefusedWhileTheEditorHoldsUnsavedEdits()
    {
        // An audit compares the board against the file. A report computed while
        // the buffer differs from the file describes a backlog nobody has.
        var (audit, board) = Subject(Work(1, "Epic", "Epic 1"));
        audit.UnsavedEditsCheck = () => true;

        await audit.RunAsync(Workspace(Epic("Epic 1")));

        Assert.False(audit.HasReport);
        Assert.Contains("backlog.unsaved", audit.ErrorText);
        Assert.Equal(0, board.ReadCount);
    }

    [Fact]
    public async Task WithNoTokenTheAuditReportsTheCredentialProblemAndNeverReadsTheBoard()
    {
        var board = new FakeBoardGateway();
        var audit = new AuditViewModel(_ => board, new UnavailableCredentialStore("test"));

        // No session token, and the config points pat_env/pat_file at nothing.
        await audit.RunAsync(Workspace(Epic("Epic 1")));

        Assert.False(audit.HasReport);
        Assert.True(audit.HasError);
        Assert.Contains("No personal access token found", audit.ErrorText);
        Assert.Equal(0, board.ReadCount);
    }

    [Fact]
    public async Task AFailedBoardReadIsReportedWithItsTypedCode()
    {
        var board = new FakeBoardGateway
        {
            ReadError = Core.Results.Error.Authorization("board.unauthorized", "That token was rejected."),
        };

        var audit = new AuditViewModel(_ => board, new UnavailableCredentialStore("test"))
        {
            SessionToken = "bad-token",
        };

        await audit.RunAsync(Workspace(Epic("Epic 1")));

        Assert.False(audit.HasReport);
        Assert.Contains("board.unauthorized", audit.ErrorText);
    }

    [Fact]
    public async Task DiscardingClearsTheReportSoItCannotOutliveItsProfile()
    {
        var (audit, _) = Subject(Work(1, "Epic", "Epic 1"));

        await audit.RunAsync(Workspace(Epic("Epic 1")));
        Assert.True(audit.HasReport);

        audit.Discard();

        Assert.False(audit.HasReport);
        Assert.Empty(audit.Findings);
        Assert.Empty(audit.Reviews);
        Assert.False(audit.CanCloseChildren);
    }

    [Fact]
    public async Task TheReportNamesTheBoardItWasComputedAgainst()
    {
        // The fingerprint is what lets a later hand-off to a Plan say whether the
        // board has moved since the audit was read.
        var (audit, board) = Subject(Work(1, "Epic", "Epic 1"));

        await audit.RunAsync(Workspace(Epic("Epic 1")));

        var snapshot = await board.ReadAsync(Config());
        Assert.Equal(snapshot.Value.Fingerprint, audit.Report!.BoardFingerprint);
    }
}
