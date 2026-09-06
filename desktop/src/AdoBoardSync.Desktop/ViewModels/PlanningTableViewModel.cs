using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using AdoBoardSync.Core.Backlog;
using AdoBoardSync.Core.Results;
using AdoBoardSync.Desktop.Services;
using AdoBoardSync.Infrastructure;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdoBoardSync.Desktop.ViewModels;

/// <summary>
/// One row of a planning table. Codes stay as typed text — a half-typed code is an
/// ordinary state mid-edit — and are re-split only when that text changes.
/// </summary>
public abstract partial class PlanningRowViewModel : ObservableObject
{
    /// <summary>
    /// A list pasted from a spreadsheet, a chat message or the config itself arrives
    /// comma-, space- or newline-separated, and none of those is the user's mistake.
    /// </summary>
    private static readonly char[] Separators = [',', ' ', '\t', '\n', '\r', ';'];

    private IReadOnlyList<string>? _parsed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CodeCount))]
    private string _codes = string.Empty;

    public int CodeCount => ParsedCodes().Count;

    public IReadOnlyList<string> ParsedCodes() => _parsed ??=
        [.. Codes.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(code => code.ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)];

    partial void OnCodesChanged(string value) => _parsed = null;
}

/// <summary>The three coverage sentences, phrased for one table.</summary>
/// <param name="Unknown">Codes in the table that no backlog Issue carries.</param>
/// <param name="Uncovered">Backlog Issues the table does not name.</param>
/// <param name="Duplicated">Codes claimed by more than one row; the first listed wins.</param>
public sealed record CoverageWording(string Unknown, string Uncovered, string Duplicated);

/// <summary>
/// The mechanism both planning tables (ABSD-401, ABSD-402) are built from: load a
/// table out of the open profile, track whether it is dirty, write it back to
/// <c>board.config.json</c>, and report which codes each side of the plan is
/// missing.
///
/// Their one behavioural difference is <see cref="RefuseSave" />: the assignee map
/// is a dictionary, so two rows for one identity collapse on write and the losing
/// row's codes vanish. The iteration list has no such key.
///
/// Neither table writes to the board. Saving changes the profile, and the Plan is
/// then generated on the Plan surface like every other command.
/// </summary>
public abstract partial class PlanningTableViewModel<TRow> : ObservableObject
    where TRow : PlanningRowViewModel, new()
{
    private const string NoProfileStatus = "No board profile open.";

    /// <summary>
    /// Re-opens the profile after a save, through the same loader the shell uses.
    /// The composition root supplies it (see <c>AppServices.AddViewModels</c>); the
    /// fallback is for a table built outside the container.
    /// </summary>
    private readonly Func<string, Task<Result<BacklogWorkspace>>> _reload;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private bool _isDirty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private bool _isBusy;

    [ObservableProperty] private string _statusText = NoProfileStatus;

    private BacklogWorkspace? _workspace;

    /// <summary>
    /// The backlog's Issue codes, upper-cased. Derived from the open profile, which
    /// does not change between <see cref="Load" /> calls, so it is computed there
    /// rather than on every keystroke.
    /// </summary>
    private HashSet<string> _backlogCodes = new(StringComparer.Ordinal);

    /// <summary>
    /// True while <see cref="Load" /> is repopulating the table. Filling the rows
    /// raises exactly the events an edit raises, and without this a freshly loaded
    /// profile would come up already dirty and offer to save what it just read.
    /// </summary>
    private bool _loading;

    protected PlanningTableViewModel(Func<string, Task<Result<BacklogWorkspace>>>? reload = null)
    {
        _reload = reload ?? DefaultReload;
        Rows.CollectionChanged += OnCollectionChanged;
    }

    public ObservableCollection<TRow> Rows { get; } = [];

    /// <summary>Codes in the table that no backlog Issue carries, and the reverse.</summary>
    public ObservableCollection<string> CoverageNotes { get; } = [];

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    public bool HasCoverageNotes => CoverageNotes.Count > 0;

    /// <summary>
    /// A profile described in onboarding has no file to write to. Saving is refused
    /// rather than inventing a path.
    /// </summary>
    public bool CanSave => IsDirty && !IsBusy && _workspace?.ConfigPath is not null;

    public string SaveBlockedReason => _workspace is null
        ? "Open a Board profile first."
        : _workspace.ConfigPath is null
            ? $"This profile has no board.config.json on disk yet. Save the profile before editing its {PluralNoun}."
            : string.Empty;

    /// <summary>Set by the shell: called with the reloaded profile after a save.</summary>
    public Action<BacklogWorkspace>? Reloaded { get; set; }

    /// <summary>What this table edits, lowercase and plural: "sprints", "assignees".</summary>
    protected abstract string PluralNoun { get; }

    /// <summary>The rows this profile's config implies, in the order they should appear.</summary>
    protected abstract IEnumerable<TRow> RowsFrom(BacklogWorkspace workspace);

    /// <summary>Writes the table to <paramref name="path" />. Called off the UI thread.</summary>
    protected abstract Result<bool> Write(string path);

    /// <summary>The status line for a table with rows in it.</summary>
    protected abstract string LoadedStatus(int rowCount, int codeCount);

    /// <summary>The status line for a table with none.</summary>
    protected abstract string EmptyStatus { get; }

    /// <summary>How this table names each of the three coverage findings.</summary>
    protected abstract CoverageWording Wording { get; }

    /// <summary>
    /// A refusal to check before writing, or null to proceed. Returning a message
    /// here leaves the file exactly as it was.
    /// </summary>
    protected virtual string? RefuseSave() => null;

    /// <summary>Fills the table from the open profile, discarding any unsaved edits.</summary>
    public void Load(BacklogWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        _workspace = workspace;
        _backlogCodes = [.. workspace.Items
            .Where(item => item.Level == BacklogLevel.Issue && item.Code is { Length: > 0 })
            .Select(item => item.Code!.ToUpperInvariant())];

        Repopulate(RowsFrom(workspace));

        IsDirty = false;
        ErrorText = null;
        RefreshCoverage();

        StatusText = Rows.Count == 0
            ? EmptyStatus
            : LoadedStatus(Rows.Count, Rows.Sum(row => row.CodeCount));

        RaiseSaveState();
    }

    /// <summary>
    /// Empties the table, for when the profile it belonged to is closed. The row
    /// subscriptions come off with it: a row left subscribed after its profile has
    /// gone would mark the next profile's table dirty on its own.
    /// </summary>
    public void Clear()
    {
        Repopulate([]);

        _workspace = null;
        _backlogCodes = new HashSet<string>(StringComparer.Ordinal);
        IsDirty = false;
        ErrorText = null;
        RefreshCoverage();
        StatusText = NoProfileStatus;
        RaiseSaveState();
    }

    public void Add() => Rows.Add(new TRow());

    public void Remove(TRow row) => Rows.Remove(row);

    /// <summary>
    /// Writes the table back to <c>board.config.json</c> and hands the shell the
    /// reloaded profile. The write is atomic and schema-validated before it lands
    /// (see <see cref="Core.Configuration.BoardConfigWriter" />), so a rejected
    /// table leaves the file exactly as it was.
    /// </summary>
    public async Task SaveAsync()
    {
        if (_workspace is not { ConfigPath: { } path })
        {
            ErrorText = SaveBlockedReason;
            return;
        }

        if (RefuseSave() is { } refusal)
        {
            ErrorText = refusal;
            return;
        }

        IsBusy = true;
        ErrorText = null;
        StatusText = $"Saving {PluralNoun}…";

        try
        {
            var written = await Task.Run(() => Write(path));

            if (written.IsFailure)
            {
                ErrorText = $"{written.Error!.SafeMessage} ({written.Error.Code})";
                StatusText = $"Could not save the {PluralNoun}.";
                return;
            }

            var reopened = await _reload(path);
            if (reopened.IsFailure)
            {
                ErrorText = $"{reopened.Error!.SafeMessage} ({reopened.Error.Code})";
                StatusText = $"The {PluralNoun} were saved, but the profile could not be reopened.";
                return;
            }

            IsDirty = false;
            Load(reopened.Value);
            StatusText += " · saved";
            Reloaded?.Invoke(reopened.Value);
        }
        catch (Exception ex)
        {
            // Caught here rather than in each view's async void click handler, where
            // an escaping exception takes the process down.
            ErrorText = $"The {PluralNoun} were not saved: {ex.Message} (config.unsaved)";
            StatusText = $"Could not save the {PluralNoun}.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Names the codes on each side that the other does not have, and the codes
    /// claimed twice. The Plan silently skips all three.
    /// </summary>
    private void RefreshCoverage()
    {
        CoverageNotes.Clear();

        if (_workspace is not null)
        {
            // One pass over the rows answers both questions: HashSet.Add reports
            // whether the code was already claimed, so the duplicates fall out of
            // the same walk that builds the claimed set.
            var claimed = new HashSet<string>(StringComparer.Ordinal);
            var duplicated = new HashSet<string>(StringComparer.Ordinal);
            foreach (var code in Rows.SelectMany(row => row.ParsedCodes()))
            {
                if (!claimed.Add(code))
                {
                    duplicated.Add(code);
                }
            }

            Note(Wording.Unknown, claimed.Where(code => !_backlogCodes.Contains(code)));
            Note(Wording.Uncovered, _backlogCodes.Where(code => !claimed.Contains(code)));
            Note(Wording.Duplicated, duplicated);
        }

        OnPropertyChanged(nameof(HasCoverageNotes));

        void Note(string phrase, IEnumerable<string> codes)
        {
            var listed = codes.Order(StringComparer.Ordinal).ToArray();
            if (listed.Length > 0)
            {
                CoverageNotes.Add($"{phrase}: {string.Join(", ", listed)}.");
            }
        }
    }

    /// <summary>
    /// Swaps the table's contents without letting the churn mark it dirty, and
    /// unsubscribes whatever was there first. <see cref="ObservableCollection{T}.Clear" />
    /// raises Reset with no OldItems, so the unsubscribe cannot be left to
    /// <see cref="OnCollectionChanged" />.
    /// </summary>
    private void Repopulate(IEnumerable<TRow> rows)
    {
        _loading = true;
        try
        {
            foreach (var row in Rows)
            {
                row.PropertyChanged -= OnRowChanged;
            }

            Rows.Clear();
            foreach (var row in rows)
            {
                Rows.Add(row);
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private void RaiseSaveState()
    {
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(SaveBlockedReason));
    }

    /// <summary>The one place a row is subscribed, for rows added from anywhere.</summary>
    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var row in e.NewItems?.OfType<TRow>() ?? [])
        {
            row.PropertyChanged -= OnRowChanged;
            row.PropertyChanged += OnRowChanged;
        }

        foreach (var row in e.OldItems?.OfType<TRow>() ?? [])
        {
            row.PropertyChanged -= OnRowChanged;
        }

        if (_loading)
        {
            return;
        }

        IsDirty = true;
        RefreshCoverage();
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        // CodeCount is derived from Codes and raised alongside it, so reacting to
        // both would mark the table dirty twice for one keystroke.
        if (e.PropertyName == nameof(PlanningRowViewModel.CodeCount))
        {
            return;
        }

        IsDirty = true;

        // Only the codes move the coverage notes. Typing a sprint name or an
        // assignee identity still dirties the table, but recomputing coverage for
        // it would walk every row and every backlog Issue for nothing.
        if (e.PropertyName is null or nameof(PlanningRowViewModel.Codes))
        {
            RefreshCoverage();
        }
    }

    private static Task<Result<BacklogWorkspace>> DefaultReload(string path) =>
        new ProfileLoader(new FileSystemBacklogFileStore()).LoadAsync(path);
}
