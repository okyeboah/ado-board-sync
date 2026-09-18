using System.Collections.ObjectModel;
using AdoBoardSync.Core.Agents;
using AdoBoardSync.Core.Operations;
using AdoBoardSync.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdoBoardSync.Desktop.ViewModels;

/// <summary>
/// One Apply run on the timeline, with its rows loaded on demand. Rows are not
/// fetched with the list because a machine with months of history would pay for
/// every run's detail to show ten summaries.
/// </summary>
public sealed partial class OperationRunViewModel(OperationRun run) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutcomes))]
    private bool _isExpanded;

    [ObservableProperty] private bool _isLoadingOutcomes;

    public OperationRun Run { get; } = run;

    public ObservableCollection<OperationItemOutcome> Outcomes { get; } = [];

    public bool HasOutcomes => Outcomes.Count > 0;

    public string Command => Run.Command;

    /// <summary>Local time, because the reader is asking "did I do this before lunch?".
    /// The store keeps UTC so the ordering survives a machine that moved timezone.</summary>
    public string StartedAt => Run.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public string Duration => Run.FinishedAt is { } finished
        ? $"{(finished - Run.StartedAt).TotalSeconds:0.0}s"
        : "unfinished";

    /// <summary>
    /// A run with no FinishedAt was interrupted — the app stopped between the
    /// first write and the last. It is shown as such rather than hidden: the
    /// board may hold half of it, and that is precisely when someone looks here.
    /// </summary>
    public bool WasInterrupted => !Run.IsComplete;

    public string Result => WasInterrupted
        ? "Interrupted — some rows may have been written"
        : Run.Failed == 0
            ? $"{Run.Succeeded} applied"
            : $"{Run.Succeeded} applied, {Run.Failed} failed";

    /// <summary>DESIGN-SYSTEM §5.3: a glyph beside the word, never colour alone.</summary>
    public string Glyph => WasInterrupted ? "!" : Run.Failed == 0 ? "✓" : "×";

    public string Summary => Run.Summary;
}

/// <summary>How far back the timeline looks. <see cref="All"/> names no bound.</summary>
public sealed record HistorySpan(string Name, TimeSpan? Length)
{
    public static readonly HistorySpan All = new("All time", null);
    public static readonly HistorySpan Day = new("Last 24 hours", TimeSpan.FromHours(24));
    public static readonly HistorySpan Week = new("Last 7 days", TimeSpan.FromDays(7));
    public static readonly HistorySpan Month = new("Last 30 days", TimeSpan.FromDays(30));

    public override string ToString() => Name;
}

/// <summary>
/// One agent run on the timeline (ABSD-706). A change in an agent's behaviour is
/// attributable rather than guessed at, which is why the verdict is carried beside
/// the status: a run that succeeded and was then rejected is a different fact from
/// one that failed.
/// </summary>
public sealed class AgentRunViewModel
{
    private readonly AgentRunRecord _run;

    public AgentRunViewModel(AgentRunRecord run) => _run = run;

    public AgentRunRecord Run => _run;

    public string Provider => $"{_run.ProviderId} {_run.ProviderVersion}";

    /// <summary>Local time, for the same reason as the Apply timeline's.</summary>
    public string StartedAt => _run.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public string Scope => _run.ScopeLabel is { } label ? $"{_run.Scope} · {label}" : _run.Scope;

    /// <summary>Trimmed for the row; the whole prompt stays on the tooltip.</summary>
    public string Prompt => _run.Prompt.Length > 80 ? _run.Prompt[..80] + "…" : _run.Prompt;

    public string FullPrompt => _run.Prompt;

    public string Status => $"{_run.Status} (exit {_run.ExitCode})";

    /// <summary>DESIGN-SYSTEM §5.3: glyph and word together, never colour alone.</summary>
    public string Verdict => _run.EditAccepted switch
    {
        true => "✓ accepted",
        false => "× rejected",
        null => "under review",
    };
}

/// <summary>
/// The History surface (ABSD-508): every Apply this machine has run, newest
/// first, scoped to the active Board profile, filterable by command and age —
/// and, since ABSD-706, every agent run recorded against it too.
///
/// The scoping is the load-bearing part. The store holds every profile's runs in
/// one database, and a timeline that mixed two profiles would show a user writes
/// they did not make to the board they are looking at. Every read here passes the
/// active profile's key, and <see cref="Clear" /> empties the list when the profile
/// changes rather than leaving the previous one's runs on screen.
///
/// The filters narrow the page that was loaded; the store query stays the most
/// recent N runs. A run older than the page will not come back by filtering, and
/// the status line says how many rows the page holds when it is full.
/// </summary>
public sealed partial class HistoryViewModel : ObservableObject
{
    private readonly IOperationHistory _history;

    private readonly IAgentRunHistory? _agentRuns;

    private string? _profileKey;

    /// <summary>How many runs the timeline shows. The store is append-only and
    /// unbounded; a view that loaded all of it would get slower every week.</summary>
    public const int PageSize = 50;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorText;

    [ObservableProperty] private bool _isBusy;

    [ObservableProperty] private string _statusText = "No board profile open.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredRuns), new[] { nameof(HasVisibleRuns) })]
    private string _selectedCommand = AllCommands;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredRuns), new[] { nameof(HasVisibleRuns) })]
    private HistorySpan _selectedSpan = HistorySpan.All;

    /// <summary>The chooser value that means no command filter.</summary>
    public const string AllCommands = "All commands";

    public HistoryViewModel(IOperationHistory history, IAgentRunHistory? agentRuns = null)
    {
        _history = history;
        _agentRuns = agentRuns;
    }

    public ObservableCollection<OperationRunViewModel> Runs { get; } = [];

    public ObservableCollection<AgentRunViewModel> AgentRuns { get; } = [];

    public ObservableCollection<OperationRunViewModel> FilteredRuns { get; } = [];

    public IReadOnlyList<string> CommandChoices { get; private set; } = [AllCommands];

    public IReadOnlyList<HistorySpan> Spans { get; } =
        [HistorySpan.All, HistorySpan.Day, HistorySpan.Week, HistorySpan.Month];

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    public bool HasRuns => Runs.Count > 0;

    public bool HasVisibleRuns => FilteredRuns.Count > 0;

    public bool HasAgentRuns => AgentRuns.Count > 0;

    /// <summary>True once a store that holds agent runs was supplied. The section
    /// is absent rather than empty when this app has no agent history at all.</summary>
    public bool HasAgentSection => _agentRuns is not null;

    public bool IsEmpty => !IsBusy && !HasRuns && _profileKey is not null;

    /// <summary>Empties the timeline. Called when the active profile changes.</summary>
    public void Clear()
    {
        Runs.Clear();
        AgentRuns.Clear();
        RefreshFilteredRuns();
        _profileKey = null;
        ErrorText = null;
        StatusText = "No board profile open.";
        OnPropertyChanged(nameof(HasRuns));
        OnPropertyChanged(nameof(HasAgentRuns));
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>Loads the active profile's runs — applied and agent alike — newest first.</summary>
    public async Task LoadAsync(BacklogWorkspace workspace, CancellationToken cancellationToken = default)
    {
        var key = workspace.ProfileKey;

        // A profile switch must not leave the previous profile's runs visible
        // while the new ones load.
        if (!string.Equals(key, _profileKey, StringComparison.Ordinal))
        {
            Runs.Clear();
            AgentRuns.Clear();
            RefreshFilteredRuns();
            OnPropertyChanged(nameof(HasRuns));
            OnPropertyChanged(nameof(HasAgentRuns));
        }

        _profileKey = key;
        IsBusy = true;
        ErrorText = null;
        StatusText = "Reading the operation history…";

        try
        {
            var listed = await _history.ListRunsAsync(key, PageSize, cancellationToken);
            if (listed.IsFailure)
            {
                ErrorText = $"{listed.Error!.SafeMessage} ({listed.Error.Code})";
                StatusText = "Could not read the operation history.";
                return;
            }

            Runs.Clear();
            foreach (var run in listed.Value)
            {
                Runs.Add(new OperationRunViewModel(run));
            }

            if (_agentRuns is { } agentRuns)
            {
                var agentListed = await agentRuns.ListRunsAsync(key, PageSize, cancellationToken);
                if (agentListed.IsFailure)
                {
                    // A broken agent timeline must not hide the Apply runs that
                    // did load; the error says what did not.
                    ErrorText = $"{agentListed.Error!.SafeMessage} ({agentListed.Error.Code})";
                }
                else
                {
                    AgentRuns.Clear();
                    foreach (var run in agentListed.Value)
                    {
                        AgentRuns.Add(new AgentRunViewModel(run));
                    }

                    OnPropertyChanged(nameof(HasAgentRuns));
                }
            }

            CommandChoices = [AllCommands, .. Runs.Select(run => run.Command).Distinct()];
            OnPropertyChanged(nameof(CommandChoices));
            RefreshFilteredRuns();
            StatusText = Runs.Count switch
            {
                0 => "No Apply has run against this profile on this machine.",
                1 => "1 Apply run",
                var n when n >= PageSize => $"{n} most recent Apply runs",
                var n => $"{n} Apply runs",
            };
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(HasRuns));
            OnPropertyChanged(nameof(HasVisibleRuns));
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    /// <summary>Expands one run and loads its per-item outcomes the first time.</summary>
    public async Task ToggleAsync(OperationRunViewModel run, CancellationToken cancellationToken = default)
    {
        run.IsExpanded = !run.IsExpanded;

        if (!run.IsExpanded || run.Outcomes.Count > 0)
        {
            return;
        }

        run.IsLoadingOutcomes = true;
        try
        {
            var listed = await _history.ListOutcomesAsync(run.Run.Id, cancellationToken);
            if (listed.IsFailure)
            {
                ErrorText = $"{listed.Error!.SafeMessage} ({listed.Error.Code})";
                return;
            }

            foreach (var outcome in listed.Value)
            {
                run.Outcomes.Add(outcome);
            }
        }
        finally
        {
            run.IsLoadingOutcomes = false;
        }
    }

    /// <summary>Recomputes the visible page from the command and span filters.</summary>
    private void RefreshFilteredRuns()
    {
        FilteredRuns.Clear();
        foreach (var run in Runs)
        {
            if (Matches(run))
            {
                FilteredRuns.Add(run);
            }
        }

        OnPropertyChanged(nameof(HasVisibleRuns));
    }

    partial void OnSelectedCommandChanged(string value) => RefreshFilteredRuns();

    partial void OnSelectedSpanChanged(HistorySpan value) => RefreshFilteredRuns();

    private bool Matches(OperationRunViewModel run)
    {
        if (SelectedCommand != AllCommands && run.Command != SelectedCommand)
        {
            return false;
        }

        if (SelectedSpan.Length is { } length && run.Run.StartedAt < DateTimeOffset.UtcNow - length)
        {
            return false;
        }

        return true;
    }
}
