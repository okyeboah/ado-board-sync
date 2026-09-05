using System.Collections.ObjectModel;
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

/// <summary>
/// The History surface (ABSD-508): every Apply this machine has run, newest
/// first, scoped to the active Board profile.
///
/// The scoping is the load-bearing part. The store holds every profile's runs in
/// one database, and a timeline that mixed two profiles would show a user writes
/// they did not make to the board they are looking at. Every read here passes the
/// active profile's key, and <see cref="Clear"/> empties the list when the profile
/// changes rather than leaving the previous one's runs on screen.
/// </summary>
public sealed partial class HistoryViewModel(IOperationHistory history) : ObservableObject
{
    /// <summary>How many runs the timeline shows. The store is append-only and
    /// unbounded; a view that loaded all of it would get slower every week.</summary>
    public const int PageSize = 50;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorText;

    [ObservableProperty] private bool _isBusy;

    [ObservableProperty] private string _statusText = "No board profile open.";

    private string? _profileKey;

    public ObservableCollection<OperationRunViewModel> Runs { get; } = [];

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    public bool HasRuns => Runs.Count > 0;

    public bool IsEmpty => !IsBusy && !HasRuns && _profileKey is not null;

    /// <summary>Empties the timeline. Called when the active profile changes.</summary>
    public void Clear()
    {
        Runs.Clear();
        _profileKey = null;
        ErrorText = null;
        StatusText = "No board profile open.";
        OnPropertyChanged(nameof(HasRuns));
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>Loads the active profile's runs, newest first.</summary>
    public async Task LoadAsync(BacklogWorkspace workspace, CancellationToken cancellationToken = default)
    {
        var key = workspace.ProfileKey;

        // A profile switch must not leave the previous profile's runs visible
        // while the new ones load.
        if (!string.Equals(key, _profileKey, StringComparison.Ordinal))
        {
            Runs.Clear();
            OnPropertyChanged(nameof(HasRuns));
        }

        _profileKey = key;
        IsBusy = true;
        ErrorText = null;
        StatusText = "Reading the operation history…";

        try
        {
            var listed = await history.ListRunsAsync(key, PageSize, cancellationToken);
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
            var listed = await history.ListOutcomesAsync(run.Run.Id, cancellationToken);
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
}
