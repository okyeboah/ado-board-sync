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
        UiHarness.OnUiThread(() =>
        {
            using var profile = Standard();
            var window = OpenProfile(profile);
            var model = Model(window);

            // The pane needs the history store to exist at all; the composition root
            // supplies it, and this is where that wiring is checked end to end.
            Assert.NotNull(model.History);

            model.CurrentSectionIndex = 5;
            UiHarness.Pump();

            Assert.True(model.ShowHistory);
            Assert.False(model.ShowPlanned);
            Assert.True(
                UiHarness.ShowsText(window, "Nothing applied yet"),
                "An empty timeline showed nothing at all instead of saying it was empty.");

            window.Close();
        });
    }

    [Fact]
    public void AProfileOpenedInTheShellAppearsInTheProfileSwitcher()
    {
        UiHarness.OnUiThread(() =>
        {
            using var profile = Standard();
            var window = OpenProfile(profile);
            var model = Model(window);

            Assert.NotNull(model.Profiles);
            UiHarness.Pump();

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

            window.Close();
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
        UiHarness.OnUiThread(() =>
        {
            using var profile = Standard();
            var window = OpenProfile(profile);
            var model = Model(window);

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

            window.Close();
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
    {
        // ABSD-304/306, checked at the surface rather than the view model: the only
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
}
