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
/// Acceptance criteria for the packaging and platform promises (PRD-AC-16, PRD-AC-17)
/// and the later-criteria set, split from the engine criteria by file for size.
/// </summary>
public sealed partial class AcceptanceTests
{
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
