using AdoBoardSync.Desktop.ViewModels;
using AdoBoardSync.TestKit;

namespace AdoBoardSync.Desktop.Tests;

/// <summary>
/// The shell's read path: config to parsed tree to what the pane shows. The view
/// model holds no Avalonia types, so these run without a display.
/// </summary>
public class MainWindowViewModelTests
{
    private static string StandardBacklog() => RepoPaths.Fixture("backlog", "standard.md");

    [Fact]
    public async Task LoadGroupsIssuesUnderTheirEpic()
    {
        using var profile = TempBoardProfile.Create(StandardBacklog());
        var model = Shell.OnDisk();

        await model.LoadAsync(profile.ConfigPath);

        Assert.False(model.HasError);
        Assert.Equal(2, model.Nodes.Count);
        Assert.All(model.Nodes, node => Assert.True(node.IsEpic));
        Assert.Equal(["PROJ-101", "PROJ-102"], model.Nodes[0].Children.Select(c => c.Badge));
        Assert.Equal(["PROJ-201"], model.Nodes[1].Children.Select(c => c.Badge));
    }

    [Fact]
    public async Task LoadSelectsTheFirstIssueSoThePaneIsNeverBlank()
    {
        using var profile = TempBoardProfile.Create(StandardBacklog());
        var model = Shell.OnDisk();

        await model.LoadAsync(profile.ConfigPath);

        Assert.NotNull(model.SelectedNode);
        Assert.Equal("PROJ-101", model.SelectedNode!.Badge);
    }

    [Fact]
    public async Task AnIssueAboveTheFirstEpicIsDroppedExactlyAsTheCliDropsIt()
    {
        // parser.py only starts collecting once an Epic heading matches, so an
        // Issue written above the first Epic is not part of the backlog at all.
        // The tree must not invent a home for it, or the desktop app would show
        // work that `import` would never create.
        var directory = Directory.CreateTempSubdirectory("abs-desktop-orphan-").FullName;
        try
        {
            var backlog = Path.Combine(directory, "backlog.md");
            File.WriteAllText(
                backlog,
                "### PROJ-001 · Orphan before any epic\n- a task\n\n## Epic 1 — Later\n\n### PROJ-002 · Owned\n");

            using var profile = TempBoardProfile.Create(backlog);
            var model = Shell.OnDisk();

            await model.LoadAsync(profile.ConfigPath);

            var epic = Assert.Single(model.Nodes);
            Assert.Equal("Epic 1 — Later", epic.Title);
            Assert.Equal(["PROJ-002"], epic.Children.Select(c => c.Badge));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AMissingConfigBecomesAnErrorNotAnException()
    {
        var model = Shell.OnDisk();

        await model.LoadAsync(Path.Combine(Path.GetTempPath(), "absolutely-not-a-real-board.config.json"));

        Assert.True(model.HasError);
        Assert.Contains("config.not_found", model.ErrorText);
        Assert.Empty(model.Nodes);
        Assert.Null(model.SelectedNode);
    }

    [Fact]
    public async Task AMissingBacklogFileNamesTheConfigKeyToFix()
    {
        using var profile = TempBoardProfile.Create(Path.Combine(Path.GetTempPath(), "no-such-backlog.md"));
        var model = Shell.OnDisk();

        await model.LoadAsync(profile.ConfigPath);

        Assert.True(model.HasError);
        Assert.Contains("backlog.not_found", model.ErrorText);
        Assert.Contains("board_file", model.ErrorText);
    }

    [Fact]
    public async Task ReloadPicksUpAnEditMadeOutsideTheApp()
    {
        var directory = Directory.CreateTempSubdirectory("abs-desktop-reload-").FullName;
        try
        {
            var backlog = Path.Combine(directory, "backlog.md");
            File.WriteAllText(backlog, "## Epic 1 — One\n\n### PROJ-001 · First\n");

            using var profile = TempBoardProfile.Create(backlog);
            var model = Shell.OnDisk();
            await model.LoadAsync(profile.ConfigPath);
            Assert.Single(model.Nodes[0].Children);

            File.WriteAllText(backlog, "## Epic 1 — One\n\n### PROJ-001 · First\n\n### PROJ-002 · Added\n");
            await model.ReloadAsync();

            Assert.Equal(["PROJ-001", "PROJ-002"], model.Nodes[0].Children.Select(c => c.Badge));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task StatusTextReportsWhatWasParsed()
    {
        using var profile = TempBoardProfile.Create(StandardBacklog());
        var model = Shell.OnDisk();

        await model.LoadAsync(profile.ConfigPath);

        Assert.Contains("2 epics", model.StatusText);
        Assert.Contains("3 issues", model.StatusText);
        Assert.Contains("prefix PROJ", model.StatusText);
    }

    [Fact]
    public async Task ReloadWithoutALoadedProfileDoesNothing()
    {
        var model = Shell.OnDisk();

        await model.ReloadAsync();

        Assert.False(model.HasError);
        Assert.Empty(model.Nodes);
        Assert.False(model.CanReload);
    }

    // ---------------------------------------------------- editing and saving

    private const string SavedBacklog =
        "## Epic 1 — One\n\n### PROJ-001 · First\nOld description.\n- an old task\n";

    [Fact]
    public async Task AnEditMarksTheProfileUnsavedAndSaveWritesTheFile()
    {
        var directory = Directory.CreateTempSubdirectory("abs-desktop-save-").FullName;
        try
        {
            var backlog = Path.Combine(directory, "backlog.md");
            File.WriteAllText(backlog, SavedBacklog);
            using var profile = TempBoardProfile.Create(backlog);

            var model = Shell.OnDisk();
            await model.LoadAsync(profile.ConfigPath);
            var issue = model.SelectedNode!;
            Assert.False(model.HasUnsavedEdits);

            issue.SetEditedSource("New description.\n- a new task\n- another");
            Assert.True(model.HasUnsavedEdits);

            await model.SaveAsync();

            // The file holds the edit, exactly as authored in the buffer.
            Assert.Equal(
                "## Epic 1 — One\n\n### PROJ-001 · First\nNew description.\n- a new task\n- another\n",
                File.ReadAllText(backlog));
            Assert.False(model.HasUnsavedEdits);
            Assert.False(model.HasError);
            Assert.Equal(2, model.TaskCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ASaveReParsesAndKeepsTheSelectionOnTheSameItem()
    {
        var directory = Directory.CreateTempSubdirectory("abs-desktop-reselect-").FullName;
        try
        {
            var backlog = Path.Combine(directory, "backlog.md");
            File.WriteAllText(backlog, SavedBacklog);
            using var profile = TempBoardProfile.Create(backlog);

            var model = Shell.OnDisk();
            await model.LoadAsync(profile.ConfigPath);
            Assert.Equal("PROJ-001", model.SelectedNode!.Badge);

            model.SelectedNode.SetEditedSource("Rewritten.\n");
            await model.SaveAsync();

            // The tree was rebuilt; the pane must not have jumped to another item.
            Assert.Equal("PROJ-001", model.SelectedNode.Badge);
            Assert.Equal("Rewritten.", model.SelectedNode.Source);
            Assert.False(model.SelectedNode.IsDirty);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ASaveRefusesToOverwriteAnExternalChange()
    {
        var directory = Directory.CreateTempSubdirectory("abs-desktop-conflict-").FullName;
        try
        {
            var backlog = Path.Combine(directory, "backlog.md");
            File.WriteAllText(backlog, SavedBacklog);
            using var profile = TempBoardProfile.Create(backlog);

            var model = Shell.OnDisk();
            await model.LoadAsync(profile.ConfigPath);
            model.SelectedNode!.SetEditedSource("Buffer edit.\n");

            const string external = "## Epic 1 — One\n\n### PROJ-001 · First\nEdited elsewhere.\n";
            File.WriteAllText(backlog, external);

            await model.SaveAsync();

            Assert.True(model.HasError);
            Assert.Contains("backlog.changed_on_disk", model.ErrorText);
            // The external edit survived, and the buffer is still there to save later.
            Assert.Equal(external, File.ReadAllText(backlog));
            Assert.True(model.HasUnsavedEdits);
            Assert.Equal("Buffer edit.\n", model.Nodes[0].Children[0].Source);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SavingWithNothingDirtyDoesNotTouchTheFile()
    {
        var directory = Directory.CreateTempSubdirectory("abs-desktop-noop-").FullName;
        try
        {
            var backlog = Path.Combine(directory, "backlog.md");
            File.WriteAllText(backlog, SavedBacklog);
            using var profile = TempBoardProfile.Create(backlog);

            var model = Shell.OnDisk();
            await model.LoadAsync(profile.ConfigPath);
            var statusBefore = model.StatusText;

            await model.SaveAsync();

            Assert.False(model.HasError);
            Assert.Equal(statusBefore, model.StatusText);
            Assert.Equal(SavedBacklog, File.ReadAllText(backlog));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TwoEditedItemsAreSplicedBackTogether()
    {
        var directory = Directory.CreateTempSubdirectory("abs-desktop-two-").FullName;
        try
        {
            var backlog = Path.Combine(directory, "backlog.md");
            File.WriteAllText(backlog, SavedBacklog + "\n### PROJ-002 · Second\nSecond old.\n");
            using var profile = TempBoardProfile.Create(backlog);

            var model = Shell.OnDisk();
            await model.LoadAsync(profile.ConfigPath);
            model.Nodes[0].Children[0].SetEditedSource("First new.\n");
            model.Nodes[0].Children[1].SetEditedSource("Second new.\n");

            await model.SaveAsync();

            Assert.Equal(
                "## Epic 1 — One\n\n### PROJ-001 · First\nFirst new.\n\n### PROJ-002 · Second\nSecond new.\n",
                File.ReadAllText(backlog));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ThePlanGateRefusesWhileTheBufferIsDirty()
    {
        // The wiring under test: the shell hands the Plan view model the
        // unsaved-edits check, so a Plan is never computed from a stale file.
        var directory = Directory.CreateTempSubdirectory("abs-desktop-gate-").FullName;
        try
        {
            var backlog = Path.Combine(directory, "backlog.md");
            File.WriteAllText(backlog, SavedBacklog);
            using var profile = TempBoardProfile.Create(backlog);

            var model = Shell.OnDisk();
            await model.LoadAsync(profile.ConfigPath);
            model.SelectedNode!.SetEditedSource("Unsaved work.\n");

            Assert.True(model.BoardPlan.UnsavedEditsCheck!());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ---------------------------------------------------------- CSV export

    [Fact]
    public async Task OpeningFromOnboardingWithABadConfigStaysOnTheOnboardingScreen()
    {
        var model = Shell.OnDisk();

        await model.OpenFromOnboardingAsync(Path.Combine(Path.GetTempPath(), "no-such-board.config.json"));

        // First run: the first-run screen stays up and explains the failure where
        // the file was chosen, instead of replacing it with an error page.
        Assert.False(model.HasError);
        Assert.False(model.HasProfile);
        Assert.True(model.ShowOnboarding);
        Assert.True(model.Onboarding.HasImportError);
        Assert.Contains("config.not_found", model.Onboarding.ImportErrorText);

        // Opening a good one afterwards adopts it and clears the message.
        using var profile = TempBoardProfile.Create(StandardBacklog());
        await model.OpenFromOnboardingAsync(profile.ConfigPath);
        Assert.True(model.HasProfile);
        Assert.False(model.Onboarding.HasImportError);
    }

    [Fact]
    public async Task ExportCsvWritesTheGenCsvBytesWithoutACredential()
    {
        var directory = Directory.CreateTempSubdirectory("abs-desktop-csv-").FullName;
        try
        {
            var backlog = Path.Combine(directory, "backlog.md");
            File.WriteAllText(backlog, SavedBacklog);
            using var profile = TempBoardProfile.Create(backlog);

            var model = Shell.OnDisk();
            await model.LoadAsync(profile.ConfigPath);

            var destination = Path.Combine(directory, "work-items.csv");
            await model.ExportCsvToAsync(destination);

            Assert.False(model.HasError);
            Assert.True(File.Exists(destination));
            Assert.Equal(
                "Work Item Type,Title 1,Title 2,Description\r\n" +
                "Epic,Epic 1 — One,,\r\n" +
                "Issue,,PROJ-001 · First,\"<p>Old description.</p>\n<ul>\n<li>an old task</li>\n</ul>\"\r\n",
                File.ReadAllText(destination));
            Assert.Contains("Import CSV written to", model.StatusText);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
