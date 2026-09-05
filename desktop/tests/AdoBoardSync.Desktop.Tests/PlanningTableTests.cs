using AdoBoardSync.Core.Backlog;
using AdoBoardSync.Core.Configuration;
using AdoBoardSync.Core.Results;
using AdoBoardSync.Desktop.Services;
using AdoBoardSync.Desktop.ViewModels;

namespace AdoBoardSync.Desktop.Tests;

/// <summary>
/// Pins the two editable config tables: sprints (ABSD-401) and assignees
/// (ABSD-402).
///
/// Both edit the profile and neither writes to the board, which is the property
/// most worth protecting here — the Plan/Apply gate is the only path to Azure
/// DevOps, and a table that could reach it directly would have two very different
/// undo stories behind one button.
/// </summary>
public class PlanningTableTests
{
    private const string Markdown = "## Epic 1\n\n### PROJ-101 · A\n";

    private static BoardConfig Config(string extra = "") =>
        BoardConfig.Parse(
            $$"""{"org":"o","project":"p","code_prefix":"PROJ","board_file":"backlog.md"{{extra}}}""",
            Path.GetTempPath()).Value;

    private static BacklogItem Issue(string code) => new()
    {
        Level = BacklogLevel.Issue,
        Title = $"{code} · A",
        Code = code,
    };

    private static BacklogWorkspace Workspace(
        BoardConfig config, string? configPath = "/tmp/board.config.json", params BacklogItem[] items) =>
        new(configPath, config, "backlog.md", Markdown, items, 0,
            FileStamp.For(DateTimeOffset.UnixEpoch, Markdown));

    /// <summary>
    /// Stands in for the profile loader after a save. Both tables re-open the
    /// profile through the shell's loader; a test must not reach a real disk to
    /// prove what the table wrote.
    /// </summary>
    private static Func<string, Task<Result<BacklogWorkspace>>> Reopens(BoardConfig config) =>
        _ => Task.FromResult<Result<BacklogWorkspace>>(Workspace(config));

    private static SprintPlanningViewModel Sprint(
        BoardConfig config,
        Func<string, IReadOnlyList<IterationConfig>, Result<bool>>? write = null) =>
        new(write ?? ((_, _) => true), Reopens(config));

    private static AssigneePlanningViewModel Assignee(
        BoardConfig config,
        Func<string, IReadOnlyDictionary<string, IReadOnlyList<string>>, Result<bool>>? write = null) =>
        new(write ?? ((_, _) => true), Reopens(config));

    // ---------------------------------------------------------------- sprints

    private const string TwoSprints =
        ""","iterations":[{"name":"S1","start":"2026-01-05","items":["PROJ-101"]},{"name":"S2","items":[]}]""";

    [Fact]
    public void LoadingTheSprintTableDoesNotMarkItDirty()
    {
        // Filling the rows raises exactly the events an edit raises. A profile
        // that comes up dirty would offer to save what it has just read.
        var sprints = Sprint(Config(TwoSprints));

        sprints.Load(Workspace(Config(TwoSprints)));

        Assert.Equal(2, sprints.Sprints.Count);
        Assert.False(sprints.IsDirty);
        Assert.False(sprints.CanSave);
    }

    [Fact]
    public void EditingASprintMarksTheTableDirtyExactlyOnce()
    {
        var sprints = Sprint(Config(TwoSprints));
        sprints.Load(Workspace(Config(TwoSprints)));

        var dirtyRaised = 0;
        sprints.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SprintPlanningViewModel.IsDirty))
            {
                dirtyRaised++;
            }
        };

        sprints.Sprints[0].Codes = "PROJ-101, PROJ-102";

        Assert.True(sprints.IsDirty);
        Assert.Equal(1, dirtyRaised);
    }

    [Fact]
    public void ReloadingAfterAnEditRewiresTheRowsRatherThanDoublingTheirSubscriptions()
    {
        // The first version of this view model subscribed twice per Load, so the
        // second edit after a reload raised everything twice.
        var sprints = Sprint(Config(TwoSprints));
        sprints.Load(Workspace(Config(TwoSprints)));
        sprints.Sprints[0].Name = "Renamed";
        sprints.Load(Workspace(Config(TwoSprints)));

        var dirtyRaised = 0;
        sprints.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SprintPlanningViewModel.IsDirty))
            {
                dirtyRaised++;
            }
        };

        sprints.Sprints[0].Name = "Renamed again";

        Assert.Equal(1, dirtyRaised);
    }

    [Fact]
    public void CodesAreSplitOnCommasAndWhitespaceAlike()
    {
        // A list pasted from a spreadsheet, a chat message or the config itself
        // arrives in all three shapes, and none of them is the user's mistake.
        var row = new SprintRowViewModel { Codes = "proj-101, PROJ-102\nproj-103 PROJ-101" };

        Assert.Equal(["PROJ-101", "PROJ-102", "PROJ-103"], row.ParsedCodes());
        Assert.Equal(3, row.CodeCount);
    }

    [Fact]
    public void TheSprintTableNamesCodesNeitherSideHas()
    {
        var sprints = Sprint(Config(TwoSprints));

        sprints.Load(Workspace(Config(TwoSprints), items: [Issue("PROJ-101"), Issue("PROJ-900")]));
        sprints.Sprints[1].Codes = "PROJ-404";

        Assert.Contains(sprints.CoverageNotes, n => n.Contains("PROJ-404") && n.Contains("not in the backlog"));
        Assert.Contains(sprints.CoverageNotes, n => n.Contains("PROJ-900") && n.Contains("no sprint"));
    }

    [Fact]
    public void TheSprintTableWarnsWhenTwoSprintsClaimOneCode()
    {
        var sprints = Sprint(Config(TwoSprints));
        sprints.Load(Workspace(Config(TwoSprints), items: [Issue("PROJ-101")]));

        sprints.Sprints[1].Codes = "PROJ-101";

        Assert.Contains(sprints.CoverageNotes, n => n.Contains("first listed wins"));
    }

    [Fact]
    public async Task SavingSprintsWritesTheTableToTheConfigPath()
    {
        string? seenPath = null;
        IReadOnlyList<IterationConfig> seen = [];

        var sprints = Sprint(Config(TwoSprints), (path, rows) =>
        {
            seenPath = path;
            seen = rows;
            return true;
        });

        sprints.Load(Workspace(Config(TwoSprints), "/tmp/somewhere/board.config.json"));
        sprints.Sprints[0].Codes = "PROJ-101 PROJ-102";

        await sprints.SaveAsync();

        Assert.Equal("/tmp/somewhere/board.config.json", seenPath);
        Assert.Equal(["PROJ-101", "PROJ-102"], seen[0].Items);
        Assert.Equal("2026-01-05", seen[0].Start);
        Assert.Null(seen[1].Start);
    }

    [Fact]
    public async Task AProfileWithNoConfigFileRefusesToSaveRatherThanInventingAPath()
    {
        var wrote = false;
        var sprints = Sprint(Config(TwoSprints), (_, _) =>
        {
            wrote = true;
            return true;
        });

        sprints.Load(Workspace(Config(TwoSprints), configPath: null));
        sprints.Add();

        await sprints.SaveAsync();

        Assert.False(wrote);
        Assert.False(sprints.CanSave);
        Assert.Contains("no board.config.json", sprints.ErrorText);
    }

    [Fact]
    public async Task ARefusedWriteLeavesTheTableDirtySoTheEditIsNotLost()
    {
        var sprints = Sprint(Config(TwoSprints),
            (_, _) => Error.Validation("config.duplicate_iteration", "Two iterations share a name."));

        sprints.Load(Workspace(Config(TwoSprints)));
        sprints.Sprints[1].Name = "S1";

        await sprints.SaveAsync();

        Assert.True(sprints.IsDirty);
        Assert.Contains("config.duplicate_iteration", sprints.ErrorText);
        Assert.Equal("S1", sprints.Sprints[1].Name);
    }

    // -------------------------------------------------------------- assignees

    private const string OneOwner = ""","assignees":{"ada@example.com":["PROJ-101"]}""";

    [Fact]
    public void LoadingTheAssigneeTableDoesNotMarkItDirty()
    {
        var owners = Assignee(Config(OneOwner));

        owners.Load(Workspace(Config(OneOwner)));

        Assert.Equal("ada@example.com", Assert.Single(owners.Owners).Identity);
        Assert.False(owners.IsDirty);
    }

    [Fact]
    public async Task SavingAssigneesWritesTheMapToTheConfigPath()
    {
        IReadOnlyDictionary<string, IReadOnlyList<string>> seen =
            new Dictionary<string, IReadOnlyList<string>>();

        var owners = Assignee(Config(OneOwner), (_, map) =>
        {
            seen = map;
            return true;
        });

        owners.Load(Workspace(Config(OneOwner)));
        owners.Add();
        owners.Owners[1].Identity = "grace@example.com";
        owners.Owners[1].Codes = "proj-102";

        await owners.SaveAsync();

        Assert.Equal(["PROJ-101"], seen["ada@example.com"]);
        Assert.Equal(["PROJ-102"], seen["grace@example.com"]);
    }

    [Fact]
    public async Task TwoRowsForOneIdentityAreRefusedRatherThanSilentlyMerged()
    {
        // Writing them would collapse the two into one entry, and whichever row
        // lost would have its codes vanish without a word.
        var wrote = false;
        var owners = Assignee(Config(OneOwner), (_, _) =>
        {
            wrote = true;
            return true;
        });

        owners.Load(Workspace(Config(OneOwner)));
        owners.Add();
        owners.Owners[1].Identity = "ADA@example.com";
        owners.Owners[1].Codes = "PROJ-102";

        await owners.SaveAsync();

        Assert.False(wrote);
        Assert.Contains("config.duplicate_assignee", owners.ErrorText);
    }

    [Fact]
    public void TheAssigneeTableNamesUnownedBacklogIssuesAndUnknownCodes()
    {
        var owners = Assignee(Config(OneOwner));

        owners.Load(Workspace(Config(OneOwner), items: [Issue("PROJ-101"), Issue("PROJ-900")]));

        Assert.Contains(owners.CoverageNotes, n => n.Contains("PROJ-900") && n.Contains("no owner"));

        owners.Owners[0].Codes = "PROJ-101, PROJ-404";
        Assert.Contains(owners.CoverageNotes, n => n.Contains("PROJ-404") && n.Contains("not in the backlog"));
    }

    [Fact]
    public void TheAssigneeTableWarnsWhenTwoPeopleClaimOneCode()
    {
        var owners = Assignee(Config(OneOwner));
        owners.Load(Workspace(Config(OneOwner), items: [Issue("PROJ-101")]));

        owners.Add();
        owners.Owners[1].Identity = "grace@example.com";
        owners.Owners[1].Codes = "PROJ-101";

        Assert.Contains(owners.CoverageNotes, n => n.Contains("first listed wins"));
    }

    [Fact]
    public void RemovingARowStopsItFromMarkingTheTableDirty()
    {
        var owners = Assignee(Config(OneOwner));
        owners.Load(Workspace(Config(OneOwner)));

        var removed = owners.Owners[0];
        owners.Remove(removed);
        owners.IsDirty = false;

        removed.Codes = "PROJ-999";

        Assert.False(owners.IsDirty);
    }
}
