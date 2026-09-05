using System.Reflection;
using System.Text.RegularExpressions;
using AdoBoardSync.Core.Backlog;
using AdoBoardSync.Core.Board;
using AdoBoardSync.Core.Configuration;
using AdoBoardSync.Core.Planning;
using AdoBoardSync.Desktop.Services;
using AdoBoardSync.Desktop.ViewModels;
using AdoBoardSync.TestKit;

namespace AdoBoardSync.Desktop.Tests;

/// <summary>
/// One test per acceptance criterion in <c>desktop/docs/PRD.md</c> (ABSD-503).
///
/// The other suites are organised by the code they exercise, which means a
/// criterion can be covered three times over while its neighbour is covered not at
/// all, and nothing says so. This one is organised by the promise instead: each
/// test is named for the criterion it discharges and carries it as a trait, and
/// <see cref="EveryAcceptanceCriterionInThePrdHasATest" /> reads the PRD and fails
/// if a criterion has no test — or if a test claims a criterion the PRD does not
/// have, which is what happens when one is renumbered.
///
/// These are deliberately thin over the engines beneath them. A criterion is a
/// promise about behaviour a user can observe, so each test drives the surface a
/// user would drive and asserts what they would see; the exhaustive branch coverage
/// lives in the suites named after those engines.
/// </summary>
public class AcceptanceTests
{
    private const string Criterion = "Criterion";

    // ------------------------------------------------------------- fixtures

    private static string Fixture(string name) => RepoPaths.Fixture("backlog", name);

    private static string WriteBacklog(string text)
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("absd-ac-").FullName, "backlog.md");
        File.WriteAllText(path, text);
        return path;
    }

    private static async Task<BacklogWorkspace> OpenAsync(TempBoardProfile profile) =>
        await Shell.WorkspaceAsync(profile.ConfigPath);

    private static PlanViewModel Gate(FakeBoardGateway board) =>
        new(_ => board) { SessionToken = "acceptance-token" };

    private static AuditViewModel Auditor(FakeBoardGateway board) =>
        new(_ => board) { SessionToken = "acceptance-token" };

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
        {
            // The generated markup the pane shows, against the CLI's conversion of
            // the same source. Whitespace included: this is what goes on the wire.
            Assert.Equal(PythonReference.Text("html", node.Source), node.Html);
        }
    }

    // ---------------------------------------------------------- PRD-AC-03

    [Fact]
    [Trait(Criterion, "PRD-AC-03")]
    public async Task MalformedMarkupIsFlaggedByTheSameRuleAsCheckHtmlAndBlocksApply()
    {
        // The rule itself, against the CLI's: both audit the generated HTML, so the
        // desktop app fails exactly the descriptions check-html fails.
        Assert.Equal(
            PythonReference.Problems("<b>open forever"),
            Core.Markdown.HtmlBalance.Problems("<b>open forever"));

        Assert.Empty(Core.Markdown.HtmlBalance.Problems("<p>text <b>bold</b></p>"));

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
        using var history = new Infrastructure.Operations.SqliteOperationHistory(
            Path.Combine(Directory.CreateTempSubdirectory("absd-ac-history-").FullName, "history.db"));

        using var profile = TempBoardProfile.Create(
            WriteBacklog("## Epic 1\n\n### PROJ-101 · One\n\n### PROJ-102 · Two\n"));
        var workspace = await OpenAsync(profile);

        var plan = new PlanViewModel(_ => board, recorder: new ApplyHistoryRecorder(history))
        {
            SessionToken = "acceptance-token",
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

    [Fact]
    [Trait(Criterion, "PRD-AC-11")]
    public async Task ASprintPlanPutsEveryListedIssueAndItsTasksOnTheEarliestSprintThatNamesIt()
    {
        var board = new FakeBoardGateway();
        var epic = board.Seed("Epic", "Epic 1");
        var issue = board.Seed("Issue", "PROJ-101 · One", parentId: epic);
        var task = board.Seed("Task", "do the thing", parentId: issue);

        using var profile = TempBoardProfile.Create(
            WriteBacklog("## Epic 1\n\n### PROJ-101 · One\n\n- do the thing\n"),
            config =>
            {
                config["iterations"] = new System.Text.Json.Nodes.JsonArray(
                    new System.Text.Json.Nodes.JsonObject
                    {
                        ["name"] = "Sprint 1",
                        ["items"] = new System.Text.Json.Nodes.JsonArray("PROJ-101"),
                    },
                    // The same code listed again, later. The earliest listing wins.
                    new System.Text.Json.Nodes.JsonObject
                    {
                        ["name"] = "Sprint 2",
                        ["items"] = new System.Text.Json.Nodes.JsonArray("PROJ-101"),
                    });
            });

        var workspace = await OpenAsync(profile);
        var plan = Gate(board);
        plan.Choose(PlanCommand.Sprints);
        await ApplyAsync(plan, workspace);

        var after = board.Items.ToDictionary(item => item.Id);
        Assert.EndsWith("Sprint 1", after[issue].IterationPath, StringComparison.Ordinal);
        Assert.EndsWith("Sprint 1", after[task].IterationPath, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------- PRD-AC-12

    [Fact]
    [Trait(Criterion, "PRD-AC-12")]
    public async Task AnAssigneePlanOwnsEveryListedIssueAndItsTasksAndReportsSettledOnesUnchanged()
    {
        var board = new FakeBoardGateway();
        var epic = board.Seed("Epic", "Epic 1");
        var issue = board.Seed("Issue", "PROJ-101 · One", parentId: epic);
        var task = board.Seed("Task", "do the thing", parentId: issue);
        var settled = board.Seed("Issue", "PROJ-102 · Two", parentId: epic, assignedTo: "ana@example.com");

        using var profile = TempBoardProfile.Create(
            WriteBacklog("## Epic 1\n\n### PROJ-101 · One\n\n- do the thing\n\n### PROJ-102 · Two\n"),
            config =>
            {
                config["assignees"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["ana@example.com"] = new System.Text.Json.Nodes.JsonArray("PROJ-101", "PROJ-102"),
                    // The same code under a second identity. The first listed wins.
                    ["bo@example.com"] = new System.Text.Json.Nodes.JsonArray("PROJ-101"),
                };
            });

        var workspace = await OpenAsync(profile);
        var plan = Gate(board);
        plan.Choose(PlanCommand.Assign);
        await plan.GenerateAsync(workspace);

        // Already correct, so it is reported as Unchanged rather than rewritten.
        Assert.Contains(
            plan.Plan!.Rows,
            row => row.Code == "PROJ-102" && row.Operation == PlanOperation.Unchanged);

        plan.RequestApply(workspace);
        await plan.ApplyConfirmedAsync(workspace);

        var after = board.Items.ToDictionary(item => item.Id);
        Assert.Equal("ana@example.com", after[issue].AssignedTo);
        Assert.Equal("ana@example.com", after[task].AssignedTo);
        Assert.Equal("ana@example.com", after[settled].AssignedTo);
        Assert.DoesNotContain(board.Updated, write => write.Id == settled);
    }

    // ---------------------------------------------------------- PRD-AC-13

    [Fact]
    [Trait(Criterion, "PRD-AC-13")]
    public async Task APlanComputedAgainstADifferentBoardIsRefusedRatherThanApplied()
    {
        var board = new FakeBoardGateway();
        using var profile = TempBoardProfile.Create(Fixture("standard.md"));
        var workspace = await OpenAsync(profile);

        var plan = Gate(board);
        await plan.GenerateAsync(workspace);
        plan.RequestApply(workspace);

        // Somebody else writes to the board between the review and the confirmation.
        board.Seed("Epic", "Added by somebody else");

        await plan.ApplyConfirmedAsync(workspace);

        Assert.True(plan.HasError);
        Assert.Contains("stale", plan.ErrorText, StringComparison.OrdinalIgnoreCase);
        AssertNothingWritten(board);
    }

    // ---------------------------------------------------------- PRD-AC-14

    [Fact]
    [Trait(Criterion, "PRD-AC-14")]
    public async Task SwitchingProfileMixesNothingFromThePreviousOne()
    {
        using var first = TempBoardProfile.Create(Fixture("standard.md"));
        using var second = TempBoardProfile.Create(
            WriteBacklog("## Other epic\n\n### OTHER-1 · Different\n"),
            config =>
            {
                config["org"] = "other-org";
                config["project"] = "OtherProject";
                config["code_prefix"] = "OTHER";
            });

        var shell = Shell.WithSurfaces(ShellSurfaces.StandAlone());

        await shell.LoadAsync(first.ConfigPath);
        var firstTitles = shell.Nodes.Select(node => node.Item.Title).ToList();
        Assert.NotEmpty(firstTitles);

        // A Plan and a token belonging to the first profile.
        shell.BoardPlan.SessionToken = "first-profile-token";
        await shell.BoardPlan.GenerateAsync(shell.Workspace!);

        await shell.LoadAsync(second.ConfigPath);

        // Backlog items: none of the first profile's survive.
        Assert.DoesNotContain(shell.Nodes.Select(node => node.Item.Title), title => firstTitles.Contains(title));
        Assert.Equal("OTHER", shell.CodePrefix);

        // The Plan is discarded rather than carried across.
        Assert.False(shell.BoardPlan.HasPlan);
        Assert.Empty(shell.BoardPlan.Rows);

        // And the per-profile config tables hold the second profile's data.
        Assert.Empty(shell.Sprints.Sprints);
        Assert.Empty(shell.Assignees.Owners);
    }

    // ---------------------------------------------------------- PRD-AC-15

    [Fact]
    [Trait(Criterion, "PRD-AC-15")]
    public async Task AnExternalEditIsNoticedBeforeAnythingIsAttemptedAndBlocksPlanningUntilReloaded()
    {
        // The proactive half (ABSD-504). The save-time guard below only fires once
        // the user has already done the work; this is what tells them first.
        var board = new FakeBoardGateway();
        var path = WriteBacklog("## Epic 1\n\n### PROJ-101 · One\n\nOriginal body.\n");
        using var profile = TempBoardProfile.Create(path);

        var shell = Shell.WithSurfaces(new ShellSurfaces(
            Gate(board), new AuditViewModel(), new SprintPlanningViewModel(), new AssigneePlanningViewModel()));

        await shell.LoadAsync(profile.ConfigPath);
        await shell.CheckForExternalChangeAsync();
        Assert.False(shell.IsStale);

        await File.WriteAllTextAsync(path, "## Epic 1\n\n### PROJ-101 · One\n\nSomebody else wrote this.\n");
        await shell.CheckForExternalChangeAsync();

        Assert.True(shell.IsStale);
        Assert.NotEmpty(shell.ExternalChangeText);

        // Planning is refused with the reason, and the board is never read.
        await shell.BoardPlan.GenerateAsync(shell.Workspace!);
        Assert.True(shell.BoardPlan.HasError);
        Assert.Contains("backlog.stale", shell.BoardPlan.ErrorText, StringComparison.Ordinal);
        Assert.Equal(0, board.ReadCount);
        AssertNothingWritten(board);

        // The explicit reload is what clears it.
        await shell.ReloadAsync();
        Assert.False(shell.IsStale);
        Assert.Contains("Somebody else wrote this", shell.Nodes[0].Children[0].Source, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(Criterion, "PRD-AC-15")]
    public async Task ASaveStillRefusesToOverwriteAnExternalChangeAndKeepsTheBuffer()
    {
        var path = WriteBacklog("## Epic 1\n\n### PROJ-101 · One\n\nOriginal body.\n");
        using var profile = TempBoardProfile.Create(path);

        var shell = Shell.OnDisk();
        await shell.LoadAsync(profile.ConfigPath);

        var node = shell.Nodes[0].Children[0];
        node.Source = "Edited in the app.\n";
        Assert.True(shell.HasUnsavedEdits);

        // Something else writes the file after the profile was opened.
        await File.WriteAllTextAsync(
            path, "## Epic 1\n\n### PROJ-101 · One\n\nChanged on disk by somebody else.\n");

        await shell.SaveAsync();

        Assert.True(shell.HasError);
        Assert.Contains(
            "Changed on disk by somebody else", await File.ReadAllTextAsync(path), StringComparison.Ordinal);

        // The buffer survives, so the edit is not lost while the reload is made.
        Assert.True(shell.HasUnsavedEdits);

        await shell.ReloadAsync();
        Assert.False(shell.HasError);
        Assert.False(shell.HasUnsavedEdits);
    }

    // ---------------------------------------------------------- PRD-AC-16

    [Fact]
    [Trait(Criterion, "PRD-AC-16")]
    public async Task TheExportedCsvIsByteForByteTheOneGenCsvWrites()
    {
        using var profile = TempBoardProfile.Create(Fixture("standard.md"));
        var workspace = await OpenAsync(profile);

        var shell = Shell.OnDisk();
        shell.Adopt(workspace);

        var mine = Path.Combine(profile.Directory, "mine.csv");
        await shell.ExportCsvToAsync(mine);
        Assert.False(shell.HasError, shell.ErrorText);

        using var reference = PythonReference.WithConfig("csv", profile.ConfigPath);
        var expected = reference.RootElement.GetProperty("value").GetString();

        Assert.Equal(expected, await File.ReadAllTextAsync(mine));
    }

    // ---------------------------------------------------------- PRD-AC-17

    [Fact]
    [Trait(Criterion, "PRD-AC-17")]
    public void ThePackagingScriptsProduceASelfContainedBuildThatNeedsNoToolchain()
    {
        // This test cannot install a package, and does not pretend to. What it can
        // pin is the property the criterion turns on — that what is published is
        // self-contained, so a machine with no .NET toolchain can run it — and that
        // the scripts which promise it still say so. The criterion was checked
        // empirically once, by running the published binary under `env -i` with no
        // toolchain on PATH; that check is a manual step, recorded in STATUS.md,
        // not something this suite reruns.
        var build = Path.Combine(RepoPaths.Root, "desktop", "build");
        var publish = Path.Combine(build, "publish.sh");
        var package = Path.Combine(build, "package.sh");

        Assert.True(File.Exists(publish), $"{publish} is missing.");
        Assert.True(File.Exists(package), $"{package} is missing.");

        var script = File.ReadAllText(publish);
        Assert.Contains("--self-contained", script, StringComparison.Ordinal);
        Assert.Contains("PublishSingleFile=true", script, StringComparison.Ordinal);

        // Unsigned output must say so rather than looking installable.
        Assert.Contains("UNSIGNED", File.ReadAllText(package), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------- PRD-AC-18

    [Fact]
    [Trait(Criterion, "PRD-AC-18")]
    public async Task AnEditIsReflectedEverywhereAndSurvivesTheSaveWithTheSelectionIntact()
    {
        var path = WriteBacklog("## Epic 1\n\n### PROJ-101 · One\n\nOriginal body.\n");
        using var profile = TempBoardProfile.Create(path);

        var shell = Shell.OnDisk();
        await shell.LoadAsync(profile.ConfigPath);

        var node = shell.Nodes[0].Children[0];
        shell.SelectedNode = node;

        node.Source = "Edited body with a task.\n\n- a new task\n";

        // Preview, task list and markup problems all reflect the edited text —
        // recomputed from the edit, not from what the file still says.
        Assert.Contains("Edited body", node.Html, StringComparison.Ordinal);
        Assert.Contains(
            node.Preview.Blocks.SelectMany(block => block.Runs),
            run => run.Text.Contains("Edited body", StringComparison.Ordinal));
        Assert.Contains(node.Tasks, task => task.Title.Contains("a new task", StringComparison.Ordinal));
        Assert.Empty(node.Problems);
        Assert.DoesNotContain("Original body", node.Html, StringComparison.Ordinal);

        await shell.SaveAsync();

        Assert.False(shell.HasError, shell.ErrorText);
        Assert.Contains("a new task", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
        Assert.Equal(0, shell.ProblemCount);
        Assert.False(shell.HasUnsavedEdits);

        // The selection stayed on the edited item rather than jumping to the top.
        Assert.Equal("PROJ-101", shell.SelectedNode?.Item.Code);
    }

    // ---------------------------------------------------------- PRD-AC-19

    [Fact]
    [Trait(Criterion, "PRD-AC-19")]
    public async Task UnsavedEditsRefuseBothPlanAndApplyWithTheReasonGiven()
    {
        var board = new FakeBoardGateway();
        var path = WriteBacklog("## Epic 1\n\n### PROJ-101 · One\n\nBody.\n");
        using var profile = TempBoardProfile.Create(path);

        var shell = Shell.WithSurfaces(new ShellSurfaces(
            Gate(board), new AuditViewModel(), new SprintPlanningViewModel(), new AssigneePlanningViewModel()));

        await shell.LoadAsync(profile.ConfigPath);
        shell.Nodes[0].Children[0].Source = "Edited but not saved.\n";
        Assert.True(shell.HasUnsavedEdits);

        await shell.BoardPlan.GenerateAsync(shell.Workspace!);

        Assert.False(shell.BoardPlan.HasPlan);
        Assert.True(shell.BoardPlan.HasError);
        Assert.NotEmpty(shell.BoardPlan.ErrorText!);
        AssertNothingWritten(board);
    }

    // ---------------------------------------------------------- PRD-AC-20

    [Fact]
    [Trait(Criterion, "PRD-AC-20")]
    public async Task OnboardingScaffoldsAWorkingBacklogAndKeepsAnInvalidConfigOnTheFirstRunScreen()
    {
        var shell = Shell.OnDisk();

        // An existing config that fails validation: the first-run screen stays up
        // and names the failure with a typed code.
        var broken = Path.Combine(Directory.CreateTempSubdirectory("absd-ac-broken-").FullName, "board.config.json");
        await File.WriteAllTextAsync(broken, """{"org":"","project":""}""");

        await shell.OpenFromOnboardingAsync(broken);

        Assert.False(shell.HasProfile);
        Assert.True(shell.ShowOnboarding);
        Assert.NotNull(shell.Onboarding.ImportErrorText);
        Assert.Matches(new Regex(@"\([a-z]+(\.[a-z_]+)+\)"), shell.Onboarding.ImportErrorText!);
    }

    // ------------------------------------------------------------ the guard

    [Fact]
    public void EveryAcceptanceCriterionInThePrdHasATest()
    {
        // The point of the suite. Without this a criterion added to the PRD is
        // simply never tested, and nothing anywhere says so.
        var prd = File.ReadAllText(Path.Combine(RepoPaths.Root, "desktop", "docs", "PRD.md"));

        var documented = new SortedSet<string>(
            Regex.Matches(prd, @"PRD-AC-\d{2}").Select(match => match.Value), StringComparer.Ordinal);

        // Read as attribute data rather than through the attribute's own properties:
        // xunit's TraitAttribute exposes its arguments only to the framework, so
        // the constructor arguments are what is actually readable here.
        var covered = new SortedSet<string>(
            typeof(AcceptanceTests)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .SelectMany(method => method.CustomAttributes)
                .Where(attribute => attribute.AttributeType == typeof(TraitAttribute)
                                    && attribute.ConstructorArguments.Count == 2
                                    && (string?)attribute.ConstructorArguments[0].Value == Criterion)
                .Select(attribute => (string)attribute.ConstructorArguments[1].Value!),
            StringComparer.Ordinal);

        Assert.True(documented.Count > 0, "No acceptance criteria were found in the PRD.");

        var untested = documented.Except(covered).ToList();
        var unknown = covered.Except(documented).ToList();

        Assert.True(
            untested.Count == 0,
            "These acceptance criteria have no test:\n  " + string.Join("\n  ", untested));

        Assert.True(
            unknown.Count == 0,
            "These tests claim criteria the PRD does not have — renumbered or removed:\n  "
            + string.Join("\n  ", unknown));
    }
}
