using System.Collections.ObjectModel;
using System.ComponentModel;
using AdoBoardSync.Core.Backlog;
using AdoBoardSync.Core.Configuration;
using AdoBoardSync.Desktop.Services;
using AdoBoardSync.Infrastructure;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdoBoardSync.Desktop.ViewModels;

/// <summary>
/// One configured iteration, as the table edits it. Issue codes are held as the
/// text the user typed rather than a parsed list: a half-typed code is a normal
/// state mid-edit, and re-parsing on every keystroke would fight the caret.
/// Parsing happens once, on save.
/// </summary>
public sealed partial class SprintRowViewModel : ObservableObject
{
    [ObservableProperty] private string _name = string.Empty;

    [ObservableProperty] private string _start = string.Empty;

    [ObservableProperty] private string _finish = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CodeCount))]
    private string _codes = string.Empty;

    public SprintRowViewModel()
    {
    }

    public SprintRowViewModel(IterationConfig iteration)
    {
        Name = iteration.Name;
        Start = iteration.Start ?? string.Empty;
        Finish = iteration.Finish ?? string.Empty;
        Codes = string.Join(", ", iteration.Items);
    }

    public int CodeCount => ParsedCodes().Count;

    /// <summary>
    /// The codes in the text field. Split on commas and whitespace both, because a
    /// list pasted out of a spreadsheet, a chat message or the config itself
    /// arrives in all three shapes and none of them is the user's mistake.
    /// </summary>
    public IReadOnlyList<string> ParsedCodes() =>
        [.. Codes.Split([',', ' ', '\t', '\n', '\r', ';'], StringSplitOptions.RemoveEmptyEntries
                                                           | StringSplitOptions.TrimEntries)
            .Select(code => code.ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)];

    public IterationConfig ToConfig() =>
        new(Name.Trim(),
            string.IsNullOrWhiteSpace(Start) ? null : Start.Trim(),
            string.IsNullOrWhiteSpace(Finish) ? null : Finish.Trim(),
            ParsedCodes());
}

/// <summary>
/// The Sprints surface (ABSD-401): the <c>iterations</c> table, edited here and
/// written back to <c>board.config.json</c>.
///
/// This view model does not plan and does not write to the board. Saving the
/// table changes the profile — which is what the Sprints Plan is computed from —
/// and the Plan is then generated on the Plan surface like every other command.
/// Keeping the two apart is deliberate: a table that both edited the config and
/// wrote to Azure DevOps would have two very different undo stories behind one
/// button.
/// </summary>
public sealed partial class SprintPlanningViewModel : ObservableObject
{
    private readonly Func<string, IReadOnlyList<IterationConfig>, Core.Results.Result<bool>> _write;

    /// <summary>
    /// Re-opens the profile after a save. Injected rather than called statically
    /// so a test can drive the whole save path without a disk, and so this view
    /// model goes through the same loader the shell does — a second load path
    /// would be a second set of parsing rules.
    /// </summary>
    private readonly Func<string, Task<Core.Results.Result<BacklogWorkspace>>> _reload;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private bool _isDirty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private bool _isBusy;

    [ObservableProperty] private string _statusText = "No board profile open.";

    private BacklogWorkspace? _workspace;

    /// <summary>
    /// True while <see cref="Load"/> is repopulating the table. Filling the rows
    /// raises exactly the events an edit raises, and without this a freshly loaded
    /// profile would come up already dirty and offer to save what it just read.
    /// </summary>
    private bool _loading;

    public SprintPlanningViewModel(
        Func<string, IReadOnlyList<IterationConfig>, Core.Results.Result<bool>>? write = null,
        Func<string, Task<Core.Results.Result<BacklogWorkspace>>>? reload = null)
    {
        _write = write ?? BoardConfigWriter.WriteIterations;
        _reload = reload ?? (path => new ProfileLoader(new FileSystemBacklogFileStore()).LoadAsync(path));
        Sprints.CollectionChanged += OnCollectionChanged;
    }

    public ObservableCollection<SprintRowViewModel> Sprints { get; } = [];

    /// <summary>Codes in the table that no backlog Issue carries, and the reverse.</summary>
    public ObservableCollection<string> CoverageNotes { get; } = [];

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    public bool HasCoverageNotes => CoverageNotes.Count > 0;

    /// <summary>
    /// A profile described in onboarding has no file to write to. Saving is
    /// refused rather than inventing a path, because a config written somewhere
    /// nobody looks is worse than one not written at all.
    /// </summary>
    public bool CanSave => IsDirty && !IsBusy && _workspace?.ConfigPath is not null;

    public string SaveBlockedReason => _workspace is null
        ? "Open a Board profile first."
        : _workspace.ConfigPath is null
            ? "This profile has no board.config.json on disk yet. Save the profile before editing its sprints."
            : string.Empty;

    /// <summary>Set by the shell: called with the reloaded profile after a save.</summary>
    public Action<BacklogWorkspace>? Reloaded { get; set; }

    /// <summary>Fills the table from the open profile, discarding any unsaved edits.</summary>
    public void Load(BacklogWorkspace workspace)
    {
        _workspace = workspace;

        _loading = true;
        try
        {
            foreach (var row in Sprints)
            {
                row.PropertyChanged -= OnRowChanged;
            }

            Sprints.Clear();
            foreach (var iteration in workspace.Config.Iterations)
            {
                Sprints.Add(Watch(new SprintRowViewModel(iteration)));
            }
        }
        finally
        {
            _loading = false;
        }

        IsDirty = false;
        ErrorText = null;
        RefreshCoverage();

        StatusText = Sprints.Count == 0
            ? "No sprints configured yet. Add one to start scheduling."
            : $"{Sprints.Count} sprint(s) · {Sprints.Sum(s => s.CodeCount)} issue(s) scheduled";

        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(SaveBlockedReason));
    }

    public void Add()
    {
        Sprints.Add(Watch(new SprintRowViewModel()));
    }

    public void Remove(SprintRowViewModel row)
    {
        Sprints.Remove(row);
    }

    /// <summary>
    /// Writes the table back to <c>board.config.json</c> and hands the shell the
    /// reloaded profile. The write is atomic and schema-validated before it lands
    /// (see <see cref="BoardConfigWriter"/>), so a rejected table leaves the file
    /// exactly as it was.
    /// </summary>
    public async Task SaveAsync()
    {
        if (_workspace is not { ConfigPath: { } path } workspace)
        {
            ErrorText = SaveBlockedReason;
            return;
        }

        IsBusy = true;
        ErrorText = null;
        StatusText = "Saving sprints…";

        try
        {
            var rows = Sprints.Select(s => s.ToConfig()).ToArray();
            var written = await Task.Run(() => _write(path, rows));

            if (written.IsFailure)
            {
                ErrorText = $"{written.Error!.SafeMessage} ({written.Error.Code})";
                StatusText = "Could not save the sprints.";
                return;
            }

            var reopened = await _reload(path);
            if (reopened.IsFailure)
            {
                ErrorText = $"{reopened.Error!.SafeMessage} ({reopened.Error.Code})";
                StatusText = "Sprints saved, but the profile could not be reopened.";
                return;
            }

            IsDirty = false;
            Load(reopened.Value);
            StatusText += " · saved";
            Reloaded?.Invoke(reopened.Value);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Names the codes on each side that the other does not have. The Sprints Plan
    /// silently skips both — a code with no board Issue and an Issue with no
    /// sprint — so without this the table looks complete while the Plan does
    /// half of what was expected.
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
            .Where(i => i.Level == BacklogLevel.Issue && i.Code is { Length: > 0 })
            .Select(i => i.Code!.ToUpperInvariant())
            .ToHashSet(StringComparer.Ordinal);

        var scheduled = Sprints.SelectMany(s => s.ParsedCodes()).ToHashSet(StringComparer.Ordinal);

        var unknown = scheduled.Except(backlogCodes).Order(StringComparer.Ordinal).ToArray();
        if (unknown.Length > 0)
        {
            CoverageNotes.Add($"Scheduled but not in the backlog: {string.Join(", ", unknown)}.");
        }

        var unscheduled = backlogCodes.Except(scheduled).Order(StringComparer.Ordinal).ToArray();
        if (unscheduled.Length > 0)
        {
            CoverageNotes.Add($"In the backlog with no sprint: {string.Join(", ", unscheduled)}.");
        }

        var duplicated = Sprints
            .SelectMany(s => s.ParsedCodes().Select(code => (s.Name, code)))
            .GroupBy(pair => pair.code, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();

        if (duplicated.Length > 0)
        {
            // The Plan gives a repeated code to the earliest sprint that lists it.
            // Saying so here is cheaper than a user discovering it from a Plan.
            CoverageNotes.Add(
                $"In more than one sprint, and the first listed wins: {string.Join(", ", duplicated)}.");
        }

        OnPropertyChanged(nameof(HasCoverageNotes));
    }

    private SprintRowViewModel Watch(SprintRowViewModel row)
    {
        row.PropertyChanged -= OnRowChanged;
        row.PropertyChanged += OnRowChanged;
        return row;
    }

    private void OnCollectionChanged(
        object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        foreach (var row in e.NewItems?.OfType<SprintRowViewModel>() ?? [])
        {
            Watch(row);
        }

        foreach (var row in e.OldItems?.OfType<SprintRowViewModel>() ?? [])
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
        if (_loading || e.PropertyName == nameof(SprintRowViewModel.CodeCount))
        {
            return;
        }

        IsDirty = true;
        RefreshCoverage();
    }
}
