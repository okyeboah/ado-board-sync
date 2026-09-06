using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using AdoBoardSync.Core.Backlog;
using AdoBoardSync.Core.Results;
using AdoBoardSync.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdoBoardSync.Desktop.ViewModels;

/// <summary>
/// The split rule both planning tables apply to typed issue codes.
///
/// A list pasted from a spreadsheet, a chat message or the config itself arrives
/// comma-, space- or newline-separated, and none of those is the user's mistake.
/// Stated once because it is a rule the two surfaces have to agree on.
/// </summary>
public static class IssueCodeList
{
    private static readonly char[] Separators = [',', ' ', '\t', '\n', '\r', ';'];

    public static IReadOnlyList<string> Parse(string text) =>
        [.. text.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(code => code.ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)];
}

/// <summary>
/// One row of a planning table. Codes stay as typed text — a half-typed code is an
/// ordinary state mid-edit — and are parsed only when that text changes; the
/// coverage pass reads <see cref="ParsedCodes" /> three times per row per keystroke.
/// </summary>
public abstract partial class PlanningRowViewModel : ObservableObject
{
    private IReadOnlyList<string>? _parsed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CodeCount))]
    private string _codes = string.Empty;

    public int CodeCount => ParsedCodes().Count;

    public IReadOnlyList<string> ParsedCodes() => _parsed ??= IssueCodeList.Parse(Codes);

    partial void OnCodesChanged(string value) => _parsed = null;
}

/// <summary>
/// The mechanism both planning tables (ABSD-401, ABSD-402) are built from: load a
/// table out of the open profile, track whether it is dirty, write it back to
/// <c>board.config.json</c>, and report which codes each side of the plan is
/// missing.
///
/// The two tables were the same file twice — identical dirty-tracking, row
/// subscription, clear, save sequencing and coverage logic, with different nouns.
/// The nouns stay in the subclasses; the mechanism lives here.
///
/// Their one behavioural difference is <see cref="RefuseSave" />: the assignee map
/// is a dictionary, so two rows for one identity collapse on write and the losing
/// row's codes vanish. The iteration list has no such key.
///
/// Neither table writes to the board. Saving changes the profile, and the Plan is
/// then generated on the Plan surface like every other command — a table that both
/// edited the config and wrote to Azure DevOps would have two very different undo
/// stories behind one button.
/// </summary>
public abstract partial class PlanningTableViewModel<TRow> : ObservableObject
    where TRow : PlanningRowViewModel
{
    /// <summary>
    /// Re-opens the profile after a save. Injected rather than called statically so
    /// a test can drive the whole save path without a disk, and so this table goes
    /// through the same loader the shell does — a second load path would be a
    /// second set of parsing rules.
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

    protected const string NoProfileStatus = "No board profile open.";

    private BacklogWorkspace? _workspace;

    /// <summary>
    /// True while <see cref="Load" /> is repopulating the table. Filling the rows
    /// raises exactly the events an edit raises, and without this a freshly loaded
    /// profile would come up already dirty and offer to save what it just read.
    /// </summary>
    private bool _loading;

    protected PlanningTableViewModel(Func<string, Task<Result<BacklogWorkspace>>> reload)
    {
        ArgumentNullException.ThrowIfNull(reload);

        _reload = reload;
        Rows.CollectionChanged += OnCollectionChanged;
    }

    public ObservableCollection<TRow> Rows { get; } = [];

    /// <summary>Codes in the table that no backlog Issue carries, and the reverse.</summary>
    public ObservableCollection<string> CoverageNotes { get; } = [];

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    public bool HasCoverageNotes => CoverageNotes.Count > 0;

    /// <summary>
    /// A profile described in onboarding has no file to write to. Saving is refused
    /// rather than inventing a path, because a config written somewhere nobody
    /// looks is worse than one not written at all.
    /// </summary>
    public bool CanSave => IsDirty && !IsBusy && _workspace?.ConfigPath is not null;

    public string SaveBlockedReason => _workspace is null
        ? "Open a Board profile first."
        : _workspace.ConfigPath is null
            ? $"This profile has no board.config.json on disk yet. Save the profile before editing its {PluralNoun}."
            : string.Empty;

    /// <summary>Set by the shell: called with the reloaded profile after a save.</summary>
    public Action<BacklogWorkspace>? Reloaded { get; set; }

    /// <summary>The open profile, for a subclass that needs to read it. Null when none is open.</summary>
    protected BacklogWorkspace? Workspace => _workspace;

    /// <summary>What this table edits, lowercase and plural: "sprints", "assignees".</summary>
    protected abstract string PluralNoun { get; }

    /// <summary>The rows this profile's config implies, in the order they should appear.</summary>
    protected abstract IEnumerable<TRow> RowsFrom(BacklogWorkspace workspace);

    /// <summary>A blank row, for <see cref="Add" />.</summary>
    protected abstract TRow NewRow();

    /// <summary>Writes the table to <paramref name="path" />. Called off the UI thread.</summary>
    protected abstract Result<bool> Write(string path);

    /// <summary>The status line for a table with rows in it.</summary>
    protected abstract string LoadedStatus(int rowCount, int codeCount);

    /// <summary>The status line for a table with none.</summary>
    protected abstract string EmptyStatus { get; }

    /// <summary>
    /// The three coverage sentences, each phrased for this table. Returning null
    /// suppresses one, though no table does today.
    /// </summary>
    protected abstract string UnknownCodesNote(IReadOnlyList<string> codes);

    protected abstract string UncoveredCodesNote(IReadOnlyList<string> codes);

    protected abstract string DuplicatedCodesNote(IReadOnlyList<string> codes);

    /// <summary>
    /// A refusal to check before writing, or null to proceed. The assignee table
    /// uses it to reject two rows for one identity; the sprint table has nothing to
    /// add. Returning a message here leaves the file exactly as it was.
    /// </summary>
    protected virtual string? RefuseSave() => null;

    /// <summary>Fills the table from the open profile, discarding any unsaved edits.</summary>
    public void Load(BacklogWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        _workspace = workspace;

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
        IsDirty = false;
        ErrorText = null;
        CoverageNotes.Clear();
        OnPropertyChanged(nameof(HasCoverageNotes));
        StatusText = NoProfileStatus;
        RaiseSaveState();
    }

    public void Add() => Rows.Add(Watch(NewRow()));

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
            // an escaping exception takes the process down and both handlers had to
            // remember the same guard.
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
    /// claimed twice. The Plan silently skips all three — a code with no board
    /// Issue, an Issue with no entry, and the losing half of a duplicate — so
    /// without this the table looks complete while the Plan does less than
    /// expected.
    /// </summary>
    private void RefreshCoverage()
    {
        CoverageNotes.Clear();

        if (_workspace is not { } workspace)
        {
            OnPropertyChanged(nameof(HasCoverageNotes));
            return;
        }

        var backlogCodes = workspace.Items
            .Where(item => item.Level == BacklogLevel.Issue && item.Code is { Length: > 0 })
            .Select(item => item.Code!.ToUpperInvariant())
            .ToHashSet(StringComparer.Ordinal);

        var claimed = Rows.SelectMany(row => row.ParsedCodes()).ToHashSet(StringComparer.Ordinal);

        Note(UnknownCodesNote, [.. claimed.Except(backlogCodes).Order(StringComparer.Ordinal)]);
        Note(UncoveredCodesNote, [.. backlogCodes.Except(claimed).Order(StringComparer.Ordinal)]);

        var duplicated = Rows
            .SelectMany(row => row.ParsedCodes())
            .GroupBy(code => code, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Note(DuplicatedCodesNote, duplicated);

        OnPropertyChanged(nameof(HasCoverageNotes));

        void Note(Func<IReadOnlyList<string>, string> phrase, IReadOnlyList<string> codes)
        {
            if (codes.Count > 0)
            {
                CoverageNotes.Add(phrase(codes));
            }
        }
    }

    /// <summary>
    /// Swaps the table's contents without letting the churn mark it dirty, and
    /// unsubscribes whatever was there first.
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
                Rows.Add(Watch(row));
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

    private TRow Watch(TRow row)
    {
        row.PropertyChanged -= OnRowChanged;
        row.PropertyChanged += OnRowChanged;
        return row;
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var row in e.NewItems?.OfType<TRow>() ?? [])
        {
            Watch(row);
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
        // CodeCount is derived from Codes and raised alongside it, so reacting to
        // both would mark the table dirty twice for one keystroke.
        if (_loading || e.PropertyName == nameof(PlanningRowViewModel.CodeCount))
        {
            return;
        }

        IsDirty = true;
        RefreshCoverage();
    }
}
