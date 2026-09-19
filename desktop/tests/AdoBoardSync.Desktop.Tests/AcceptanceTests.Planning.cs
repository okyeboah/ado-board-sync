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
/// Acceptance criteria for sprint, assignee, profile and editing promises
/// (PRD-AC-11 through PRD-AC-15), split from the engine criteria by file for size.
/// </summary>
public sealed partial class AcceptanceTests
{
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

        // A fake board, not the stand-alone surfaces: with no gateway supplied this
        // test built a real AzureDevOpsGateway and called dev.azure.com on every
        // run, passing because the failure landed in ErrorText, which it never
        // asserted on. The subject here is profile isolation, not board reading.
        var board = new FakeBoardGateway();
        var shell = Shell.WithSurfaces(new ShellSurfaces(
            Gate(board), new AuditViewModel(), new SprintPlanningViewModel(), new AssigneePlanningViewModel()));

        await shell.LoadAsync(first.ConfigPath);
        var firstTitles = shell.Nodes.Select(node => node.Item.Title).ToList();
        Assert.NotEmpty(firstTitles);

        // A Plan and a token belonging to the first profile.
        await shell.BoardPlan.GenerateAsync(shell.Workspace!);
        Assert.True(shell.BoardPlan.HasPlan, shell.BoardPlan.ErrorText);

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
}
