using AdoBoardSync.Desktop.ViewModels;

using AdoBoardSync.TestKit;

using Avalonia.Controls;

namespace AdoBoardSync.Desktop.Tests;

/// <summary>Shell interaction tests for the later surfaces: profile switcher, agent pane, history pane.</summary>
public sealed partial class ShellInteractionTests
{
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
                    Core.Operations.ProfileKey.For(
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
