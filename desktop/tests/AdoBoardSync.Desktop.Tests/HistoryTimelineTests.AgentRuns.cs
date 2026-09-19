using AdoBoardSync.Core.Agents;

using AdoBoardSync.Core.Backlog;

using AdoBoardSync.Core.Configuration;

using AdoBoardSync.Core.Operations;

using AdoBoardSync.Core.Planning;

using AdoBoardSync.Core.Results;

using AdoBoardSync.Desktop.Services;

using AdoBoardSync.Desktop.ViewModels;

namespace AdoBoardSync.Desktop.Tests;

/// <summary>History-timeline tests for the agent-run half of the store.</summary>
public sealed partial class HistoryTimelineTests
{
    [Fact]
    public async Task SwitchingProfileDoesNotLeaveThePreviousProfilesRunsOnScreen()
    {
        var history = new FakeHistory();
        await history.BeginRunAsync("o/p", "Import", Noon);

        var timeline = new HistoryViewModel(history);
        await timeline.LoadAsync(Workspace(Config()));
        Assert.Single(timeline.Runs);

        await timeline.LoadAsync(Workspace(Config("other", "board")));

        Assert.Empty(timeline.Runs);
        Assert.True(timeline.IsEmpty);
    }

    [Fact]
    public async Task RunsAreNewestFirst()
    {
        var history = new FakeHistory();
        await history.BeginRunAsync("o/p", "Import", Noon);
        await history.BeginRunAsync("o/p", "Resync", Noon.AddHours(1));

        var timeline = new HistoryViewModel(history);
        await timeline.LoadAsync(Workspace(Config()));

        Assert.Equal(["Resync", "Import"], timeline.Runs.Select(r => r.Command));
    }

    [Fact]
    public async Task AnInterruptedRunIsShownAsInterruptedRatherThanHidden()
    {
        // The board may hold half of it, and that is precisely when someone looks.
        var history = new FakeHistory();
        await history.BeginRunAsync("o/p", "Import", Noon);

        var timeline = new HistoryViewModel(history);
        await timeline.LoadAsync(Workspace(Config()));

        var run = Assert.Single(timeline.Runs);
        Assert.True(run.WasInterrupted);
        Assert.Equal("!", run.Glyph);
        Assert.Contains("Interrupted", run.Result);
        Assert.Equal("unfinished", run.Duration);
    }

    [Fact]
    public async Task OutcomesLoadOnlyWhenARunIsExpanded()
    {
        var history = new FakeHistory();
        var recorder = new ApplyHistoryRecorder(history);
        await recorder.BeginAsync("o/p", PlanCommand.Import, Noon);
        await recorder.RecordAsync(Outcome(true), Noon);
        await recorder.CompleteAsync("Applied 1 change.", Noon);

        var timeline = new HistoryViewModel(history);
        await timeline.LoadAsync(Workspace(Config()));

        var run = Assert.Single(timeline.Runs);
        Assert.False(run.HasOutcomes);

        await timeline.ToggleAsync(run);

        Assert.True(run.IsExpanded);
        Assert.Equal("PROJ-101 · A", Assert.Single(run.Outcomes).Title);

        // Collapsing and expanding again must not fetch or duplicate them.
        await timeline.ToggleAsync(run);
        await timeline.ToggleAsync(run);
        Assert.Single(run.Outcomes);
    }

    [Fact]
    public async Task AnUnreadableHistoryIsReportedWithItsTypedCode()
    {
        var history = new FakeHistory
        {
            ListError = Error.SourceFailure("history.unreadable", "The database is locked."),
        };

        var timeline = new HistoryViewModel(history);
        await timeline.LoadAsync(Workspace(Config()));

        Assert.True(timeline.HasError);
        Assert.Contains("history.unreadable", timeline.ErrorText);
    }

    [Fact]
    public async Task AProfileWithNoRunsSaysSoRatherThanLookingBroken()
    {
        var timeline = new HistoryViewModel(new FakeHistory());

        await timeline.LoadAsync(Workspace(Config()));

        Assert.True(timeline.IsEmpty);
        Assert.Contains("No Apply has run", timeline.StatusText);
    }

    [Fact]
    public void ClearingTheTimelineEmptiesItAndForgetsTheProfile()
    {
        var timeline = new HistoryViewModel(new FakeHistory());

        timeline.Clear();

        Assert.Empty(timeline.Runs);
        Assert.False(timeline.IsEmpty);
    }

    // ------------------------------------------------------------ ABSD-508 filters

    [Fact]
    public async Task TheCommandFilterNarrowsTheTimelineAndAllCommandsRestoresIt()
    {
        var history = new FakeHistory();
        await history.BeginRunAsync("o/p", "Import", Noon);
        await history.BeginRunAsync("o/p", "ResyncTasks", Noon.AddMinutes(1));

        var timeline = new HistoryViewModel(history);
        await timeline.LoadAsync(Workspace(Config()));

        Assert.Equal(2, timeline.FilteredRuns.Count);
        Assert.Equal(["All commands", "ResyncTasks", "Import"], timeline.CommandChoices);

        timeline.SelectedCommand = "Import";
        Assert.Equal(["Import"], timeline.FilteredRuns.Select(run => run.Command));
        Assert.True(timeline.HasVisibleRuns);

        timeline.SelectedCommand = "Audit";
        Assert.Empty(timeline.FilteredRuns);
        Assert.False(timeline.HasVisibleRuns);
        Assert.True(timeline.HasRuns, "Filtering must not lose the loaded page.");

        timeline.SelectedCommand = HistoryViewModel.AllCommands;
        Assert.Equal(2, timeline.FilteredRuns.Count);
    }

    [Fact]
    public async Task TheSpanFilterHidesRunsOlderThanItsLength()
    {
        var history = new FakeHistory();
        await history.BeginRunAsync("o/p", "Import", DateTimeOffset.UtcNow - TimeSpan.FromDays(40));
        await history.BeginRunAsync("o/p", "Import", DateTimeOffset.UtcNow - TimeSpan.FromHours(2));

        var timeline = new HistoryViewModel(history);
        await timeline.LoadAsync(Workspace(Config()));

        timeline.SelectedSpan = HistorySpan.Week;
        var visible = Assert.Single(timeline.FilteredRuns);
        Assert.True(visible.Run.StartedAt > DateTimeOffset.UtcNow - TimeSpan.FromDays(7));

        timeline.SelectedSpan = HistorySpan.All;
        Assert.Equal(2, timeline.FilteredRuns.Count);
    }

    // ------------------------------------------------------------ ABSD-706 readback

    private sealed class FakeAgentHistory : IAgentRunHistory
    {
        public List<AgentRunRecord> Runs { get; } = [];

        public Task<Result<long>> RecordRunAsync(
            AgentRunRecord record, CancellationToken cancellationToken = default)
        {
            Runs.Add(record);
            return Task.FromResult<Result<long>>(Runs.Count);
        }

        public Task<Result<bool>> RecordVerdictAsync(
            long runId, bool accepted, DateTimeOffset finishedAt,
            CancellationToken cancellationToken = default)
        {
            var index = (int)runId - 1;
            Runs[index] = Runs[index] with { EditAccepted = accepted, FinishedAt = finishedAt };
            return Task.FromResult<Result<bool>>(true);
        }

        public Task<Result<IReadOnlyList<AgentRunRecord>>> ListRunsAsync(
            string profileKey, int limit, CancellationToken cancellationToken = default)
        {
            AgentRunRecord[] matching =
            [
                .. Runs.Where(r => r.ProfileKey == profileKey)
                    .OrderByDescending(r => r.StartedAt)
                    .Take(limit)
            ];
            return Task.FromResult<Result<IReadOnlyList<AgentRunRecord>>>(matching);
        }
    }

    private static AgentRunRecord AgentRun(
        string profileKey = "o/p",
        bool? accepted = true,
        DateTimeOffset? startedAt = null) =>
        new()
        {
            ProfileKey = profileKey,
            ProviderId = "claude",
            ProviderVersion = "2.1.0",
            Prompt = "Add a runbook task to every Issue.",
            Scope = "Issue",
            ScopeLabel = "PROJ-101",
            StartedAt = startedAt ?? Noon,
            Status = "Completed",
            ExitCode = 0,
            EditAccepted = accepted,
            Summary = "1 issue's description rewritten",
        };

    [Fact]
    public async Task TheTimelineListsTheProfileSAgentRunsWithTheirVerdicts()
    {
        var history = new FakeAgentHistory();
        history.Runs.Add(AgentRun(accepted: true));
        history.Runs.Add(AgentRun(accepted: false, startedAt: Noon.AddMinutes(5)));

        var timeline = new HistoryViewModel(new FakeHistory(), history);
        await timeline.LoadAsync(Workspace(Config()));

        Assert.True(timeline.HasAgentSection);
        Assert.Equal(2, timeline.AgentRuns.Count);

        var newest = timeline.AgentRuns[0];
        Assert.Equal("claude 2.1.0", newest.Provider);
        Assert.Equal("× rejected", newest.Verdict);
        Assert.Equal("Issue · PROJ-101", newest.Scope);

        Assert.Equal("✓ accepted", timeline.AgentRuns[1].Verdict);
    }

    [Fact]
    public async Task AnUnreviewedAgentRunIsShownAsUnderReviewRatherThanGuessed()
    {
        var history = new FakeAgentHistory();
        history.Runs.Add(AgentRun(accepted: null));

        var timeline = new HistoryViewModel(new FakeHistory(), history);
        await timeline.LoadAsync(Workspace(Config()));

        Assert.Equal("under review", Assert.Single(timeline.AgentRuns).Verdict);
    }

    [Fact]
    public async Task AgentRunsAreScopedToTheActiveProfileLikeApplyRuns()
    {
        var history = new FakeAgentHistory();
        history.Runs.Add(AgentRun());
        history.Runs.Add(AgentRun(profileKey: "other/board"));

        var timeline = new HistoryViewModel(new FakeHistory(), history);
        await timeline.LoadAsync(Workspace(Config()));

        Assert.Single(timeline.AgentRuns);

        await timeline.LoadAsync(Workspace(Config("other", "board")));

        Assert.Single(timeline.AgentRuns);
        Assert.Equal("other/board", timeline.AgentRuns[0].Run.ProfileKey);
    }

    [Fact]
    public async Task ClearEmptiesTheAgentTimelineWithTheApplyTimeline()
    {
        var history = new FakeAgentHistory();
        history.Runs.Add(AgentRun());

        var timeline = new HistoryViewModel(new FakeHistory(), history);
        await timeline.LoadAsync(Workspace(Config()));
        Assert.Single(timeline.AgentRuns);

        timeline.Clear();

        Assert.Empty(timeline.AgentRuns);
        Assert.False(timeline.HasAgentRuns);
    }

    [Fact]
    public async Task AShellWithoutAnAgentStoreOffersNoAgentSectionRatherThanAnEmptyOne()
    {
        var timeline = new HistoryViewModel(new FakeHistory());
        await timeline.LoadAsync(Workspace(Config()));

        Assert.False(timeline.HasAgentSection);
        Assert.False(timeline.HasAgentRuns);
    }
}
