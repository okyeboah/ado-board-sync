using System.Reflection;
using AdoBoardSync.Core.Configuration;
using AdoBoardSync.Core.Markdown;
using AdoBoardSync.Core.Planning;
using AdoBoardSync.Desktop.Services;
using AdoBoardSync.Desktop.ViewModels;
using AdoBoardSync.Infrastructure.Operations;
using AdoBoardSync.TestKit;

namespace AdoBoardSync.Desktop.Tests;

/// <summary>
///     One test per acceptance criterion in <c>desktop/docs/PRD.md</c> (ABSD-503).
///     The other suites are organised by the code they exercise, which means a
///     criterion can be covered three times over while its neighbour is covered not at
///     all, and nothing says so. This one is organised by the promise instead: each
///     test is named for the criterion it discharges and carries it as a trait, and
///     <see cref="EveryAcceptanceCriterionInThePrdHasATest" /> reads the PRD and fails
///     if a criterion has no test — or if a test claims a criterion the PRD does not
///     have, which is what happens when one is renumbered.
///     These are deliberately thin over the engines beneath them. A criterion is a
///     promise about behaviour a user can observe, so each test drives the surface a
///     user would drive and asserts what they would see; the exhaustive branch coverage
///     lives in the suites named after those engines.
/// </summary>
public partial class AcceptanceTests
{
    private const string Criterion = "Criterion";

    // ------------------------------------------------------------- fixtures

    private static string Fixture(string name)
    {
        return RepoPaths.Fixture("backlog", name);
    }

    private static string WriteBacklog(string text)
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("absd-ac-").FullName, "backlog.md");
        File.WriteAllText(path, text);
        return path;
    }

    private static async Task<BacklogWorkspace> OpenAsync(TempBoardProfile profile)
    {
        return await Shell.WorkspaceAsync(profile.ConfigPath);
    }

    private static PlanViewModel Gate(FakeBoardGateway board)
    {
        return new PlanViewModel(_ => board) { SessionToken = "acceptance-token" };
    }

    private static AuditViewModel Auditor(FakeBoardGateway board)
    {
        return new AuditViewModel(_ => board) { SessionToken = "acceptance-token" };
    }

    /// <summary>Generates, confirms and applies — the whole gate, as a user walks it.</summary>
    private static async Task ApplyAsync(PlanViewModel plan, BacklogWorkspace workspace)
    {
        await plan.GenerateAsync(workspace);
        plan.RequestApply(workspace);
        Assert.True(plan.IsConfirming, "Apply did not ask for confirmation.");
        await plan.ApplyConfirmedAsync(workspace);
    }

    private static void AssertNothingWritten(FakeBoardGateway board)
    {
        Assert.Empty(board.Created);
        Assert.Empty(board.Updated);
        Assert.Empty(board.Deleted);
    }

    // ---------------------------------------------------------- PRD-AC-01

    [Fact]
    [Trait(Criterion, "PRD-AC-01")]
    public async Task TheParsedTreeIsTheOneTheCliWouldProduce()
    {
        using var profile = TempBoardProfile.Create(Fixture("standard.md"));
        var workspace = await OpenAsync(profile);

        // The CLI's own parse of the same file and config, through the parity
        // driver — not a recorded snapshot, so a change to either side breaks here.
        using var reference = PythonReference.WithConfig("parse", profile.ConfigPath);
        var expected = reference.RootElement.GetProperty("items");

        Assert.Equal(expected.GetArrayLength(), workspace.Items.Count);

        for (var i = 0; i < workspace.Items.Count; i++)
        {
            var cli = expected[i];
            var mine = workspace.Items[i];

            Assert.Equal(cli.GetProperty("level").GetString(), mine.Level.ToString().ToLowerInvariant());
            Assert.Equal(cli.GetProperty("title").GetString(), mine.Title);

            // Epics carry no code at all in the CLI's item dict, rather than a null
            // one — so its absence is the assertion, not a null value.
            Assert.Equal(
                cli.TryGetProperty("code", out var code) ? code.GetString() : null,
                mine.Code);
        }
    }

    // ---------------------------------------------------------- PRD-AC-02

    [Fact]
    [Trait(Criterion, "PRD-AC-02")]
    public async Task ThePreviewShowsTheHtmlTheCliWouldWrite()
    {
        using var profile = TempBoardProfile.Create(Fixture("standard.md"));
        var workspace = await OpenAsync(profile);

        var shell = Shell.OnDisk();
        shell.Adopt(workspace);

        foreach (var node in shell.Nodes)
            // The generated markup the pane shows, against the CLI's conversion of
            // the same source. Whitespace included: this is what goes on the wire.
            Assert.Equal(PythonReference.Text("html", node.Source), node.Html);
    }

    // ---------------------------------------------------------- PRD-AC-03

    /// <summary>
    ///     The workspace is built by hand on purpose, not for want of a shorter route.
    ///     PRD-AC-03 guards the converter's output, and both implementations escape raw
    ///     angle brackets unconditionally, so no authored description can produce
    ///     unbalanced HTML — there is no editor input that reaches this gate. Driving it
    ///     from typed text would prove the escaping, not the gate.
    /// </summary>
    [Fact]
    [Trait(Criterion, "PRD-AC-03")]
    public async Task MalformedMarkupIsFlaggedByTheSameRuleAsCheckHtmlAndBlocksApply()
    {
        // The rule itself, against the CLI's: both audit the generated HTML, so the
        // desktop app fails exactly the descriptions check-html fails.
        Assert.Equal(
            PythonReference.Problems("<b>open forever"),
            HtmlBalance.Problems("<b>open forever"));

        Assert.Empty(HtmlBalance.Problems("<p>text <b>bold</b></p>"));

        // Worth stating, because it is why the fixture below is built by hand: a
        // backlog description cannot currently produce unbalanced HTML at all. The
        // converter escapes raw angle brackets, so "<b>" typed into a description
        // reaches the board as text. The gate is a guard on the converter, not on
        // what a user can type today.
        using var profile = TempBoardProfile.Create(
            WriteBacklog("## Epic 1\n\n### PROJ-101 · One\n\nA <b>bold start with no end.\n"));
        var clean = await OpenAsync(profile);
        Assert.Equal(0, clean.MarkupProblemCount);

        // A workspace that does carry problems: Apply is refused before a
        // confirmation is ever offered, and the reason names the count.
        var blocked = clean with { MarkupProblemCount = 2 };

        var board = new FakeBoardGateway();
        var plan = Gate(board);
        await plan.GenerateAsync(blocked);
        plan.RequestApply(blocked);

        Assert.False(plan.IsConfirming);
        Assert.True(plan.HasError);
        Assert.Contains("markup.invalid", plan.ErrorText, StringComparison.Ordinal);
        AssertNothingWritten(board);
    }

    // ---------------------------------------------------------- PRD-AC-04

    [Fact]
    [Trait(Criterion, "PRD-AC-04")]
    public async Task APlanStatesItsExactCountsBeforeAnythingIsWritten()
    {
        var board = new FakeBoardGateway();
        board.Seed("Epic", "Epic 1");

        using var profile = TempBoardProfile.Create(
            WriteBacklog("## Epic 1\n\n### PROJ-101 · New issue\n\nBody.\n"));
        var workspace = await OpenAsync(profile);

        var plan = Gate(board);
        await plan.GenerateAsync(workspace);

        Assert.True(plan.HasPlan);
        var computed = plan.Plan!;

        // The four counts are on screen, and they add up to the rows shown.
        Assert.Equal(
            computed.Rows.Count,
            computed.CreateCount + computed.UpdateCount + computed.DeleteCount + computed.UnchangedCount);
        Assert.Equal(computed.Rows.Count, plan.Rows.Count);
        Assert.NotEmpty(plan.PlanSummary);

        AssertNothingWritten(board);
    }

    // ---------------------------------------------------------- PRD-AC-05

    [Fact]
    [Trait(Criterion, "PRD-AC-05")]
    public async Task NothingMutatingIsSentUntilApplyIsConfirmed()
    {
        var board = new FakeBoardGateway();
        using var profile = TempBoardProfile.Create(Fixture("standard.md"));
        var workspace = await OpenAsync(profile);

        var plan = Gate(board);
        await plan.GenerateAsync(workspace);
        Assert.True(plan.HasWork, "The fixture planned no work, so this would pass vacuously.");

        // Everything short of confirming: shown, asked for, then cancelled.
        plan.RequestApply(workspace);
        plan.CancelApply();
        AssertNothingWritten(board);

        // And applying without the confirmation standing is refused outright.
        await plan.ApplyConfirmedAsync(workspace);
        AssertNothingWritten(board);
    }

    // ---------------------------------------------------------- PRD-AC-06

    [Fact]
    [Trait(Criterion, "PRD-AC-06")]
    public async Task AuditNamesTheDoneParentAndItsOpenDescendant()
    {
        var board = new FakeBoardGateway();
        var epic = board.Seed("Epic", "Epic 1", state: "Done");
        board.Seed("Issue", "PROJ-101 · Still open", parentId: epic, state: "Active");

        using var profile = TempBoardProfile.Create(
            WriteBacklog("## Epic 1\n\n### PROJ-101 · Still open\n"));
        var workspace = await OpenAsync(profile);

        var audit = Auditor(board);
        await audit.RunAsync(workspace);

        Assert.True(audit.HasReport);
        var finding = Assert.Single(audit.Report!.OpenDescendantsOfDone);

        // The exact items, not a count: "something is wrong somewhere" is not a
        // report a user can act on. The parent is named by its board id, and the
        // descendant is carried so close-children can plan exactly it.
        Assert.Contains($"#{epic}", finding.Detail, StringComparison.Ordinal);
        Assert.Equal([board.Items.Single(i => i.WorkItemType == "Issue").Id], finding.BoardIds);
        Assert.True(audit.CanCloseChildren);
    }

    // ---------------------------------------------------------- PRD-AC-07

    [Fact]
    [Trait(Criterion, "PRD-AC-07")]
    public void TheDesktopPlanAndTheCliPlanAgree()
    {
        // The comparison itself is PlanParityTests: it runs each command through
        // both implementations and compares the board each left behind. This
        // asserts that gate exists and is not empty, so deleting it fails the
        // acceptance suite rather than quietly removing the guarantee.
        var parity = typeof(PlanParityTests)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => method.GetCustomAttributes<FactAttribute>().Any()
                             || method.GetCustomAttributes<TheoryAttribute>().Any())
            .ToList();

        Assert.True(
            parity.Count >= 8,
            $"The plan-computation parity gate has shrunk to {parity.Count} tests.");
    }

    // ---------------------------------------------------------- PRD-AC-08

    [Fact]
    [Trait(Criterion, "PRD-AC-08")]
    public async Task AppliedChangesAppearInTheHistoryWithEveryItemsOutcome()
    {
        var board = new FakeBoardGateway();
        using var history = new SqliteOperationHistory(
            Path.Combine(Directory.CreateTempSubdirectory("absd-ac-history-").FullName, "history.db"));

        using var profile = TempBoardProfile.Create(
            WriteBacklog("## Epic 1\n\n### PROJ-101 · One\n\n### PROJ-102 · Two\n"));
        var workspace = await OpenAsync(profile);

        var plan = new PlanViewModel(_ => board, recorder: new ApplyHistoryRecorder(history))
        {
            SessionToken = "acceptance-token"
        };

        await plan.GenerateAsync(workspace);

        // Counted from the reviewed Plan, not from the view model's Outcomes
        // collection. That collection is filled through a Progress<T>, which
        // delivers on the dispatcher and so lags the Apply it describes — reading
        // it here would make this test's verdict depend on the scheduler rather
        // than on what reached the store.
        var expected = plan.Plan!.WriteRows.Count;
        Assert.True(expected > 0, "The fixture planned no writes, so this would pass vacuously.");

        plan.RequestApply(workspace);
        await plan.ApplyConfirmedAsync(workspace);

        var timeline = new HistoryViewModel(history);
        await timeline.LoadAsync(workspace);

        var run = Assert.Single(timeline.Runs);
        Assert.False(run.WasInterrupted);
        Assert.Equal("Import", run.Command);
        Assert.Equal(expected, run.Run.Succeeded);

        // What changed, when, and the outcome of every affected item.
        await timeline.ToggleAsync(run);
        Assert.Equal(expected, run.Outcomes.Count);
        Assert.All(run.Outcomes, outcome => Assert.NotEmpty(outcome.Title));
        Assert.Equal(
            Enumerable.Range(0, expected), run.Outcomes.Select(outcome => outcome.Sequence));
    }

    // ---------------------------------------------------------- PRD-AC-09

    [Fact]
    [Trait(Criterion, "PRD-AC-09")]
    public async Task ClosingChildrenLeavesAnAlreadyAssignedItemAloneAndGivesTheRestTheAncestorsOwner()
    {
        var board = new FakeBoardGateway();
        var epic = board.Seed("Epic", "Epic 1", state: "Done", assignedTo: "ana@example.com");
        var owned = board.Seed(
            "Issue", "PROJ-101 · Already owned", parentId: epic, state: "Active", assignedTo: "bo@example.com");
        var unowned = board.Seed("Issue", "PROJ-102 · Nobody", parentId: epic, state: "Active");

        using var profile = TempBoardProfile.Create(
            WriteBacklog("## Epic 1\n\n### PROJ-101 · Already owned\n\n### PROJ-102 · Nobody\n"));
        var workspace = await OpenAsync(profile);

        var plan = Gate(board);
        plan.Choose(PlanCommand.CloseChildren);
        plan.AssignFromParent = true;
        await ApplyAsync(plan, workspace);

        var after = board.Items.ToDictionary(item => item.Id);

        // The already-assigned item keeps its owner; only the unassigned one
        // inherits — the rule --assign-from-parent applies.
        Assert.Equal("bo@example.com", after[owned].AssignedTo);
        Assert.Equal("ana@example.com", after[unowned].AssignedTo);
    }

    // ---------------------------------------------------------- PRD-AC-10

    [Fact]
    [Trait(Criterion, "PRD-AC-10")]
    public async Task WithNoResolvableTokenEveryBoardActionIsBlockedAndTheSourcesAreNamed()
    {
        var board = new FakeBoardGateway();
        using var profile = TempBoardProfile.Create(Fixture("standard.md"));
        var workspace = await OpenAsync(profile);

        // No session token, and a config naming an environment variable that is not
        // set — so nothing resolves.
        var plan = new PlanViewModel(
            _ => board, new UnavailableCredentialStore("no store in the acceptance run"));

        await plan.GenerateAsync(workspace);

        // Blocked before the board is even read, not merely reported afterwards.
        Assert.True(plan.HasError);
        Assert.Equal(0, board.ReadCount);
        AssertNothingWritten(board);

        // And it says which sources it checked, so the user knows where to put one.
        await plan.RefreshCredentialStatusAsync(workspace.Config);
        Assert.NotEmpty(plan.CredentialStatus);
    }

    // ---------------------------------------------------------- PRD-AC-11
}