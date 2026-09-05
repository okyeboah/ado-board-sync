using System.Collections.ObjectModel;
using System.ComponentModel;
using AdoBoardSync.Core.Backlog;
using AdoBoardSync.Core.Configuration;
using AdoBoardSync.Desktop.Services;
using AdoBoardSync.Infrastructure;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdoBoardSync.Desktop.ViewModels;

/// <summary>
/// One owner and the Issue codes they hold. As with the sprint table, the codes
/// stay as typed text until save: a half-typed code is an ordinary state mid-edit.
/// </summary>
public sealed partial class AssigneeRowViewModel : ObservableObject
{
    [ObservableProperty] private string _identity = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CodeCount))]
    private string _codes = string.Empty;

    public AssigneeRowViewModel()
    {
    }

    public AssigneeRowViewModel(string identity, IReadOnlyList<string> codes)
    {
        Identity = identity;
        Codes = string.Join(", ", codes);
    }

    public int CodeCount => ParsedCodes().Count;

    public IReadOnlyList<string> ParsedCodes() =>
        [.. Codes.Split([',', ' ', '\t', '\n', '\r', ';'], StringSplitOptions.RemoveEmptyEntries
                                                           | StringSplitOptions.TrimEntries)
            .Select(code => code.ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)];
}

/// <summary>
/// The Assignees surface (ABSD-402): the <c>assignees</c> map, edited here and
/// written back to <c>board.config.json</c>.
///
/// Like the sprint table it edits the profile and never the board. Azure DevOps
/// has no backlog-driven ownership — assignment is a per-item field set by hand —
/// so this table is what makes a planned work split reproducible and reviewable
/// before the Assign Plan ever runs.
/// </summary>
public sealed partial class AssigneePlanningViewModel : ObservableObject
{
    private readonly Func<string, IReadOnlyDictionary<string, IReadOnlyList<string>>, Core.Results.Result<bool>> _write;

    /// <summary>See <see cref="SprintPlanningViewModel"/>: the profile is re-opened
    /// through the shell's own loader so there is one set of parsing rules.</summary>
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

    /// <summary>See <see cref="SprintPlanningViewModel"/>: filling the table raises
    /// the same events an edit raises, and a freshly loaded profile must not come
    /// up dirty.</summary>
    private bool _loading;

    public AssigneePlanningViewModel(
        Func<string, IReadOnlyDictionary<string, IReadOnlyList<string>>, Core.Results.Result<bool>>? write = null,
        Func<string, Task<Core.Results.Result<BacklogWorkspace>>>? reload = null)
    {
        _write = write ?? BoardConfigWriter.WriteAssignees;
        _reload = reload ?? (path => new ProfileLoader(new FileSystemBacklogFileStore()).LoadAsync(path));
        Owners.CollectionChanged += OnCollectionChanged;
    }

    public ObservableCollection<AssigneeRowViewModel> Owners { get; } = [];

    public ObservableCollection<string> CoverageNotes { get; } = [];

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    public bool HasCoverageNotes => CoverageNotes.Count > 0;

    public bool CanSave => IsDirty && !IsBusy && _workspace?.ConfigPath is not null;

    public string SaveBlockedReason => _workspace is null
        ? "Open a Board profile first."
        : _workspace.ConfigPath is null
            ? "This profile has no board.config.json on disk yet. Save the profile before editing its assignees."
            : string.Empty;

    public Action<BacklogWorkspace>? Reloaded { get; set; }

    public void Load(BacklogWorkspace workspace)
    {
        _workspace = workspace;

        _loading = true;
        try
        {
            foreach (var row in Owners)
            {
                row.PropertyChanged -= OnRowChanged;
            }

            Owners.Clear();
            foreach (var (identity, codes) in workspace.Config.Assignees.OrderBy(a => a.Key, StringComparer.Ordinal))
            {
                Owners.Add(Watch(new AssigneeRowViewModel(identity, codes)));
            }
        }
        finally
        {
            _loading = false;
        }

        IsDirty = false;
        ErrorText = null;
        RefreshCoverage();

        StatusText = Owners.Count == 0
            ? "No assignees configured yet. Add one to plan ownership."
            : $"{Owners.Count} owner(s) · {Owners.Sum(o => o.CodeCount)} issue(s) owned";

        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(SaveBlockedReason));
    }

    /// <summary>
    /// Empties the table, for when the profile it belonged to is closed. The row
    /// subscriptions come off with it: a row left subscribed after its profile has
    /// gone would mark the next profile's table dirty on its own.
    /// </summary>
    public void Clear()
    {
        _loading = true;
        try
        {
            foreach (var row in Owners)
            {
                row.PropertyChanged -= OnRowChanged;
            }

            Owners.Clear();
        }
        finally
        {
            _loading = false;
        }

        _workspace = null;
        IsDirty = false;
        ErrorText = null;
        CoverageNotes.Clear();
        OnPropertyChanged(nameof(HasCoverageNotes));
        StatusText = "No board profile open.";
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(SaveBlockedReason));
    }

    public void Add()
    {
        Owners.Add(Watch(new AssigneeRowViewModel()));
    }

    public void Remove(AssigneeRowViewModel row)
    {
        Owners.Remove(row);
    }

    public async Task SaveAsync()
    {
        if (_workspace is not { ConfigPath: { } path })
        {
            ErrorText = SaveBlockedReason;
            return;
        }

        var duplicate = Owners
            .GroupBy(o => o.Identity.Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);

        if (duplicate is not null)
        {
            // Two rows for one identity would silently collapse into one entry on
            // write, and the codes in whichever row lost would vanish without a word.
            ErrorText =
                $"\"{duplicate.Key}\" appears in two rows. Merge them — one identity holds one list of codes. (config.duplicate_assignee)";
            return;
        }

        IsBusy = true;
        ErrorText = null;
        StatusText = "Saving assignees…";

        try
        {
            var map = Owners.ToDictionary(
                o => o.Identity.Trim(),
                o => o.ParsedCodes(),
                StringComparer.Ordinal);

            var written = await Task.Run(() => _write(path, map));

            if (written.IsFailure)
            {
                ErrorText = $"{written.Error!.SafeMessage} ({written.Error.Code})";
                StatusText = "Could not save the assignees.";
                return;
            }

            var reopened = await _reload(path);
            if (reopened.IsFailure)
            {
                ErrorText = $"{reopened.Error!.SafeMessage} ({reopened.Error.Code})";
                StatusText = "Assignees saved, but the profile could not be reopened.";
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
    /// The Assign Plan silently skips a configured code with no board Issue, and
    /// leaves a board Issue with no configured owner alone. Both are legitimate;
    /// both are surprising if the table does not say so.
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

        var owned = Owners.SelectMany(o => o.ParsedCodes()).ToHashSet(StringComparer.Ordinal);

        var unknown = owned.Except(backlogCodes).Order(StringComparer.Ordinal).ToArray();
        if (unknown.Length > 0)
        {
            CoverageNotes.Add($"Owned but not in the backlog: {string.Join(", ", unknown)}.");
        }

        var unowned = backlogCodes.Except(owned).Order(StringComparer.Ordinal).ToArray();
        if (unowned.Length > 0)
        {
            CoverageNotes.Add($"In the backlog with no owner: {string.Join(", ", unowned)}.");
        }

        var shared = Owners
            .SelectMany(o => o.ParsedCodes())
            .GroupBy(code => code, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();

        if (shared.Length > 0)
        {
            // The Plan gives a shared code to the first listed owner, mirroring the
            // sprint table's rule. Saying it here beats discovering it from a Plan.
            CoverageNotes.Add(
                $"Owned by more than one person, and the first listed wins: {string.Join(", ", shared)}.");
        }

        OnPropertyChanged(nameof(HasCoverageNotes));
    }

    private AssigneeRowViewModel Watch(AssigneeRowViewModel row)
    {
        row.PropertyChanged -= OnRowChanged;
        row.PropertyChanged += OnRowChanged;
        return row;
    }

    private void OnCollectionChanged(
        object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        foreach (var row in e.NewItems?.OfType<AssigneeRowViewModel>() ?? [])
        {
            Watch(row);
        }

        foreach (var row in e.OldItems?.OfType<AssigneeRowViewModel>() ?? [])
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
        if (_loading || e.PropertyName == nameof(AssigneeRowViewModel.CodeCount))
        {
            return;
        }

        IsDirty = true;
        RefreshCoverage();
    }
}
