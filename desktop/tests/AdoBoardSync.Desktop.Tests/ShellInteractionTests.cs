using AdoBoardSync.Desktop.ViewModels;
using AdoBoardSync.TestKit;
using Avalonia.Controls;

namespace AdoBoardSync.Desktop.Tests;

/// <summary>
/// The shell driven through its own controls (ABSD-108).
///
/// These are the tests a view-model suite cannot write. The window is really
/// constructed, really laid out and really clicked, so a handler that is not wired,
/// or a two-way binding written one-way, fails here and nowhere else.
///
/// What <see cref="UiHarness.BindingFailures" /> adds is narrower than it first
/// looks, and worth stating precisely. A compiled binding — one under an
/// <c>x:DataType</c> — is checked by the XAML compiler, so a mistyped property
/// there is already a build error. What is left unchecked is everything the
/// compiler cannot type: the <c>$parent[Window].DataContext</c> paths, untyped item
/// templates, and mistakes of timing where the path is right but is evaluated when
/// there is nothing behind it. That last category is not hypothetical — this
/// assertion is what found the shell assigning its DataContext after
/// InitializeComponent, which made every binding in the window resolve against null
/// once on the way up.
///
/// Everything runs on one UI thread through <see cref="UiHarness.OnUiThread" />,
/// and every window is closed: a leaked window keeps its view model alive, and its
/// bindings keep evaluating against the next test's data.
/// </summary>
public class ShellInteractionTests
{
    private static MainWindow OpenProfile(TempBoardProfile profile)
    {
        var window = new MainWindow();
        window.Show();
        window.LoadProfile(profile.ConfigPath);
        UiHarness.Pump();
        return window;
    }

    private static TempBoardProfile Standard() =>
        TempBoardProfile.Create(RepoPaths.Fixture("backlog", "standard.md"));

    private static MainWindowViewModel Model(MainWindow window) =>
        (MainWindowViewModel)window.DataContext!;

    [Fact]
    public void EveryPaneBindsToSomethingThatExists()
    {
        // The regression this exists for: a pane wired into the shell whose XAML
        // asks for a property nobody has. Walking every section is what makes it
        // catch the pane that was added last rather than only the ones on screen.
        UiHarness.OnUiThread(() =>
        {
            using var profile = Standard();
            MainWindow? window = null;

            var failures = UiHarness.BindingFailures(() =>
            {
                window = OpenProfile(profile);
                var model = Model(window);

                for (var section = 0; section < model.Sections.Count; section++)
                {
                    model.CurrentSectionIndex = section;
                    UiHarness.Pump();
                }
            });

            window?.Close();

            Assert.True(
                failures.Count == 0,
                "The shell reported binding failures:\n  " + string.Join("\n  ", failures));
        });
    }

    [Fact]
    public void TheNavRailOffersEverySectionAndSelectingOneShowsIt()
    {
        UiHarness.OnUiThread(() =>
        {
            using var profile = Standard();
            var window = OpenProfile(profile);
            var model = Model(window);

            var rail = UiHarness.Only<ListBox>(window, list => list.Name == "NavList", "the nav rail");
            Assert.Equal(model.Sections.Count, rail.ItemCount);

            // Selected through the control, not the view model: the two-way binding
            // between the rail's index and the shown pane is the thing under test.
            rail.SelectedIndex = 3;
            UiHarness.Pump();

            Assert.Equal(3, model.CurrentSectionIndex);
            Assert.True(model.ShowSprints);
            Assert.False(model.ShowPlanned);

            window.Close();
        });
    }

    [Fact]
    public void TheSprintsPaneShowsTheProfilesIterationsAndEditingOneReachesTheViewModel()
    {
        UiHarness.OnUiThread(() =>
        {
            using var profile = Standard();
            var window = OpenProfile(profile);
            var model = Model(window);

            model.CurrentSectionIndex = 3;
            UiHarness.Pump();

            // Adding through the button proves the click handler is wired; a row
            // appearing proves the ItemsControl is bound to the right collection.
            var before = model.Sprints.Sprints.Count;
            UiHarness.Click(UiHarness.Button(window, "Add sprint"));
            Assert.Equal(before + 1, model.Sprints.Sprints.Count);

            var boxes = UiHarness.All<TextBox>(window);
            Assert.NotEmpty(boxes);

            // The name box of the row just added. Typing into it must reach the row,
            // which is what a one-way binding would silently fail to do.
            var row = model.Sprints.Sprints[^1];
            var nameBox = UiHarness.Only<TextBox>(
                window, box => ReferenceEquals(box.DataContext, row) && box.Text == string.Empty
                               && box.PlaceholderText is null,
                "the new row's iteration name box");

            UiHarness.Type(nameBox, "Sprint 9");

            Assert.Equal("Sprint 9", row.Name);
            Assert.True(model.Sprints.IsDirty);

            window.Close();
        });
    }

    [Fact]
    public void TheAssigneesPaneEditsReachTheViewModelToo()
    {
        UiHarness.OnUiThread(() =>
        {
            using var profile = Standard();
            var window = OpenProfile(profile);
            var model = Model(window);

            model.CurrentSectionIndex = 4;
            UiHarness.Pump();

            UiHarness.Click(UiHarness.Button(window, "Add owner"));
            var row = Assert.Single(model.Assignees.Owners);

            var identity = UiHarness.Only<TextBox>(
                window,
                box => ReferenceEquals(box.DataContext, row)
                       && box.PlaceholderText is { } hint && hint.StartsWith("name@", StringComparison.Ordinal),
                "the new owner's identity box");

            UiHarness.Type(identity, "ana@example.com");

            Assert.Equal("ana@example.com", row.Identity);
            Assert.True(model.Assignees.IsDirty);

            window.Close();
        });
    }

    [Fact]
    public void SavingAConfigTableIsRefusedWhileThereIsNothingToSaveAndTheReasonIsOnScreen()
    {
        // A disabled button with no explanation is the state users file bugs about.
        UiHarness.OnUiThread(() =>
        {
            using var profile = Standard();
            var window = OpenProfile(profile);
            var model = Model(window);

            model.CurrentSectionIndex = 3;
            UiHarness.Pump();

            var save = UiHarness.Button(window, "Save to board.config.json");
            Assert.False(save.IsEnabled);
            Assert.False(model.Sprints.CanSave);
            Assert.True(
                UiHarness.ShowsText(window, model.Sprints.SaveBlockedReason),
                "The pane did not say why saving was unavailable.");

            window.Close();
        });
    }

    [Fact]
    public void TheHistoryPaneOpensAndReportsAnEmptyTimelineRatherThanNothing()
    {
        UiHarness.AwaitOnUiThread(async () =>
        {
            using var profile = Standard();
            var window = new MainWindow();
            try
            {
                window.Show();
                var model = Model(window);
                await model.LoadAsync(profile.ConfigPath);

                // The pane needs the history store to exist at all; the composition
                // root supplies it, and this is where that wiring is checked end to end.
                Assert.NotNull(model.History);

                model.CurrentSectionIndex = 5;
                UiHarness.Pump();

                // The timeline read is a fire-and-forget call inside Adopt; the
                // pane reports emptiness only once it has asked the store.
                await UiHarness.WaitUntilAsync(
                    () => model.History!.IsEmpty,
                    "The timeline never asked the store for the open profile's runs.");

                Assert.True(model.ShowHistory);
                Assert.False(model.ShowPlanned);
                Assert.True(
                    UiHarness.ShowsText(window, "Nothing applied yet"),
                    "An empty timeline showed nothing at all instead of saying it was empty.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void AProfileOpenedInTheShellAppearsInTheProfileSwitcher()
    {
        UiHarness.AwaitOnUiThread(async () =>
        {
            using var profile = Standard();
            var window = new MainWindow();
            try
            {
                window.Show();
                var model = Model(window);
                await model.LoadAsync(profile.ConfigPath);

                Assert.NotNull(model.Profiles);

                // Registration is a fire-and-forget write inside Adopt.
                await UiHarness.WaitUntilAsync(
                    () => model.Profiles is { Profiles.Count: 1 },
                    model.Profiles?.ErrorText is null
                        ? "The opened profile was never registered with the switcher."
                        : model.Profiles.ErrorText);

            // Say why, not just that. This collection can come back without the
            // profile for three reasons that "filter not matched" cannot tell
            // apart: the profile never loaded, the registry refused the entry, or
            // something threw on the way. Asserting both messages first means a red
            // run on a machine nobody can reach names its own cause -- which is
            // what this test failed to do on the ubuntu runner while every macOS
            // run stayed green.
            // Asserted as True-with-message rather than Null: the point is to read the
            // reason off a runner nobody can attach to, and Assert.Null reports only
            // "Value is not null", which names the assertion instead of the cause.
                Assert.True(model.ErrorText is null, model.ErrorText);
                Assert.True(model.Profiles!.ErrorText is null, model.Profiles!.ErrorText);

                var switcher = UiHarness.Only<ComboBox>(window, box => box.Name == "ProfileSwitcher", "the switcher");

                Assert.Contains(
                    model.Profiles!.Profiles,
                    row => string.Equals(row.ConfigPath, profile.ConfigPath, StringComparison.Ordinal));
                Assert.Equal(model.Profiles.Profiles.Count, switcher.ItemCount);
                Assert.Same(model.Profiles.ActiveProfile, switcher.SelectedItem);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void TheAgentPaneOpensAndStatesWhatItWillRunBeforeItRunsAnything()
    {
        // ABSD-703. The three disclosures are on screen while the user is deciding,
        // not behind a tooltip — they are about to hand a local binary a directory.
        UiHarness.OnUiThread(() =>
        {
            using var profile = Standard();
            var window = OpenProfile(profile);
            var model = Model(window);

            Assert.NotNull(model.Agent);

            model.CurrentSectionIndex = 6;
            UiHarness.Pump();

            Assert.True(model.ShowAgent);
            Assert.False(model.ShowPlanned);

            foreach (var statement in new[]
                     {
                         model.Agent!.ProviderStatement,
                         model.Agent.ReadStatement,
                         model.Agent.ChangeStatement,
                     })
            {
                Assert.True(
                    UiHarness.ShowsText(window, statement),
                    $"The pane did not show: {statement}");
            }

            window.Close();
        });
    }

    [Fact]
    public void TheAgentPaneWillNotRunWithoutAPromptAndScopesItselfToTheSelection()
    {
        UiHarness.AwaitOnUiThread(async () =>
        {
            using var profile = Standard();
            var window = new MainWindow();
            try
            {
                window.Show();
                var model = Model(window);
                await model.LoadAsync(profile.ConfigPath);

                model.CurrentSectionIndex = 6;
                UiHarness.Pump();

            // No prompt yet, so the run button is disabled rather than absent —
            // a user needs to see what they have not filled in.
            var run = UiHarness.Button(window, "Run the agent");
            Assert.False(run.IsEnabled);

            // The rail's selection is the agent's scope, so the sentence describes
            // the item the user is looking at rather than the whole backlog.
            var issue = model.Nodes[0].Children[0];
            model.SelectedNode = issue;
            UiHarness.Pump();

                Assert.Equal(Core.Agents.AgentScope.Issue, model.Agent!.Scope);
                Assert.Contains(
                    issue.Item.Code!, model.Agent.ScopeStatement, StringComparison.Ordinal);
                Assert.True(UiHarness.ShowsText(window, model.Agent.ScopeStatement));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void TheAgentPaneOffersNoPathToTheBoardOfItsOwn()
    {
        // ABSD-705, checked at the surface. The only board-facing control is the
        // request that opens the Plan, and it is not offered until an edit has been
        // accepted — there is nothing new to plan before that.
        UiHarness.OnUiThread(() =>
        {
            using var profile = Standard();
            var window = OpenProfile(profile);
            var model = Model(window);

            model.CurrentSectionIndex = 6;
            UiHarness.Pump();

            var captions = UiHarness.All<Button>(window)
                .Where(UiHarness.IsShown)
                .Select(button => button.Content as string)
                .Where(caption => caption is not null)
                .ToList();

            Assert.Contains("Run the agent", captions);
            Assert.DoesNotContain(captions, caption => caption!.Contains("Apply", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain("Generate a Plan", captions);
            Assert.False(model.Agent!.CanPlan);

            window.Close();
        });
    }

    [Fact]
    public void TheAuditPaneStillOffersNoWriteOfItsOwn()
    {        // ABSD-304/306, checked at the surface rather than the view model: the only
        // buttons on the read-only pane are the audit itself and the handoff that
        // asks for a Plan. An Apply reachable from here would be a second, ungated
        // path to the board.
        UiHarness.OnUiThread(() =>
        {
            using var profile = Standard();
            var window = OpenProfile(profile);
            var model = Model(window);

            model.CurrentSectionIndex = 2;
            UiHarness.Pump();

            var captions = UiHarness.All<Button>(window)
                .Where(UiHarness.IsShown)
                .Select(button => button.Content as string)
                .Where(caption => caption is not null)
                .ToList();

            Assert.Contains("Run audit", captions);
            Assert.DoesNotContain(captions, caption => caption!.Contains("Apply", StringComparison.OrdinalIgnoreCase));

            window.Close();
        });
    }

    // ---------------------------------------------------- ABSD-504, at the view

    [Fact]
    public void TheStaleBannerAppearsWhenTheFileChangesAndReloadClearsIt()
    {
        // The poll is the view model's; this asserts the banner the user actually
        // sees is bound to it — a stale flag with no banner would guard nothing.
        UiHarness.AwaitOnUiThread(async () =>
        {
            var directory = Directory.CreateTempSubdirectory("abs-shell-stale-").FullName;
            try
            {
                var backlog = Path.Combine(directory, "backlog.md");
                File.WriteAllText(backlog, "## Epic 1\n\n### PROJ-101 · One\n\nBody.\n");
                using var profile = TempBoardProfile.Create(backlog);

                var window = new MainWindow(Shell.OnDisk());
                try
                {
                    window.Show();
                    var model = Model(window);

                    // Awaited, not the fire-and-forget LoadProfile: the external
                    // write below must land after the profile was read, or the
                    // stamp it is compared against already holds the new bytes.
                    await model.LoadAsync(profile.ConfigPath);
                    UiHarness.Pump();
                    Assert.False(model.IsStale);

                    File.WriteAllText(backlog, "## Epic 1\n\n### PROJ-101 · One\n\nChanged elsewhere.\n");
                    await model.CheckForExternalChangeAsync();
                    UiHarness.Pump();

                    Assert.True(model.IsStale);
                    Assert.True(
                        UiHarness.ShowsText(window, model.ExternalChangeText),
                        "The profile was stale, but the banner did not show it.");

                    await model.ReloadAsync();
                    UiHarness.Pump();

                    Assert.False(model.IsStale);
                    // An empty needle is contained in everything, so the gone
                    // banner is asserted by its own words, not by the empty text.
                    Assert.False(
                        UiHarness.ShowsText(window, "changed on disk"),
                        "The banner is still up after the reload.");
                    Assert.Contains(
                        "Changed elsewhere.",
                        model.Nodes[0].Children[0].Source,
                        StringComparison.Ordinal);
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        });
    }

    // ---------------------------------------------------- ABSD-502, at the view

    [Fact]
    public void ForgettingTheOpenProfileRemovesItFromTheSwitcherAndLeavesTheFile()
    {
        UiHarness.AwaitOnUiThread(async () =>
        {
            var directory = Directory.CreateTempSubdirectory("abs-shell-forget-").FullName;
            try
            {
                using var profile = Standard();

                // A registry over the test's own file: the parameterless window
                // constructor resolves the user's real profiles.json, and this
                // test removes an entry from it.
                var registry = new ProfileRegistryViewModel(
                    new Infrastructure.Configuration.JsonProfileRegistryStore(
                        Path.Combine(directory, "profiles.json")));
                var window = new MainWindow(Shell.WithSurfaces(
                    ShellSurfaces.StandAlone() with { Profiles = registry }));
                try
                {
                    window.Show();
                    var model = Model(window);
                    await model.LoadAsync(profile.ConfigPath);

                // Registration is a fire-and-forget write inside Adopt, so the
                // switcher fills a beat after the load reports done.
                UiHarness.WaitUntil(
                    () => model.Profiles is { Profiles.Count: 1 },
                    "The opened profile was never registered with the switcher.");

                UiHarness.Click(UiHarness.Button(window, "Forget this profile"));
                var profiles = model.Profiles!;
                UiHarness.WaitUntil(
                    () => profiles.Profiles.Count == 0,
                    profiles.ErrorText ?? "Forgetting the profile did not remove it.");

                Assert.True(
                    profiles.ErrorText is null,
                    profiles.ErrorText ?? string.Empty);
                Assert.Empty(profiles.Profiles);
                Assert.True(
                    File.Exists(profile.ConfigPath),
                    "Forgetting a profile must not touch the board.config.json it pointed at.");
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        });
    }

    // ---------------------------------------------------- ABSD-508, at the view

    /// <summary>A history holding one completed run, over its own temp database —
    /// never the user's real store, which a default-constructed window resolves.</summary>
    private static async Task<Infrastructure.Operations.SqliteOperationHistory> WithRecordedRunAsync(
        string directory, string profileKey)
    {
        var history = new Infrastructure.Operations.SqliteOperationHistory(
            Path.Combine(directory, "history.db"));
        var runId = (await history.BeginRunAsync(profileKey, "Import", DateTimeOffset.UtcNow)).Value;
        var recorded = await history.RecordOutcomeAsync(runId, new Core.Operations.OperationItemOutcome
        {
            Sequence = 0,
            Operation = "Create",
            Level = "Issue",
            Code = "PROJ-101",
            Title = "PROJ-101 · One",
            BoardId = 42,
            Succeeded = true,
            Message = "Created #42",
        });
        Assert.True(recorded.IsSuccess, recorded.Error?.SafeMessage);
        var completed = await history.CompleteRunAsync(runId, DateTimeOffset.UtcNow, 1, 0, "Import · 1 created");
        Assert.True(completed.IsSuccess, completed.Error?.SafeMessage);

        var agentRun = await history.RecordRunAsync(new Core.Agents.AgentRunRecord
        {
            ProfileKey = profileKey,
            ProviderId = "claude",
            ProviderVersion = "2.1.0",
            Prompt = "Add a runbook task to every Issue.",
            Scope = "Backlog",
            StartedAt = DateTimeOffset.UtcNow,
            Status = "Completed",
            ExitCode = 0,
            EditAccepted = null,
            Summary = "1 issue's description rewritten",
        });
        Assert.True(agentRun.IsSuccess, agentRun.Error?.SafeMessage);
        return history;
    }

    [Fact]
    public void TheHistoryPaneRendersARecordedRunAndExpandsItsOutcomes()
    {
        // ABSD-508 at the view: a recorded run is on the timeline, and expanding
        // it shows the per-item outcomes — not just an empty pane that says so.
        // The store is the test's own temp database; the parameterless window
        // constructor resolves the user's real one, so the shell is built by hand
        // with a surfaces bundle that carries the store under test.
        UiHarness.AwaitOnUiThread(async () =>
        {
            var directory = Directory.CreateTempSubdirectory("abs-shell-history-").FullName;
            try
            {
                using var profile = Standard();
                await using var history = await WithRecordedRunAsync(
                    directory,
                    Infrastructure.Operations.ProfileKey.For(
                        Core.Configuration.BoardConfig.Load(profile.ConfigPath).Value));

                var window = new MainWindow(Shell.WithSurfaces(ShellSurfaces.StandAlone() with
                {
                    History = new HistoryViewModel(history, history),
                }));
                try
                {
                    window.Show();
                    var model = Model(window);
                    await model.LoadAsync(profile.ConfigPath);
                    Assert.NotNull(model.History);

                    model.CurrentSectionIndex = 5;
                    UiHarness.Pump();

                    // The timeline load is a fire-and-forget read inside Adopt.
                    UiHarness.WaitUntil(
                        () => model.History.HasRuns,
                        model.History.ErrorText ?? "The recorded run never reached the timeline.");
                    Assert.True(
                        UiHarness.ShowsText(window, "Import"),
                        "The pane showed no run for the profile it was applied against.");

                    // Expanding the run loads its outcomes on demand; the header
                    // button's content is a layout, so it is found by data context.
                    var run = Assert.Single(model.History.Runs);
                    UiHarness.Click(UiHarness.Only<Avalonia.Controls.Button>(
                        window,
                        button => UiHarness.IsShown(button)
                                  && ReferenceEquals(button.DataContext, run),
                        "the run header"));
                    UiHarness.WaitUntil(
                        () => run.Outcomes.Count > 0,
                        "Expanding the run did not load its outcomes.");

                    Assert.True(
                        UiHarness.ShowsText(window, "Created #42"),
                        "The expanded run did not show the recorded outcome.");

                    // ABSD-706 at the view: the recorded agent run is on the same
                    // timeline, with its provider and its verdict.
                    Assert.True(
                        UiHarness.ShowsText(window, "Agent runs"),
                        "The pane offered no agent section.");
                    Assert.True(
                        UiHarness.ShowsText(window, "claude 2.1.0"),
                        "The recorded agent run never reached the timeline.");
                    Assert.True(
                        UiHarness.ShowsText(window, "under review"),
                        "The agent run's verdict was not shown.");
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        });
    }
}
