using System.Collections.ObjectModel;
using System.ComponentModel;
using AdoBoardSync.Core.Backlog;
using AdoBoardSync.Core.Planning;
using AdoBoardSync.Core.Results;
using AdoBoardSync.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdoBoardSync.Desktop.ViewModels;

/// <summary>
/// One entry in the nav rail. A section that is not built yet says so in its own
/// words rather than opening an empty screen.
/// </summary>
public sealed record NavSection(string Name, string Glyph, bool IsAvailable, string Caption, string PlannedDetail);

/// <summary>
/// The shell view model: one Board profile at a time, parsed with the same Core
/// engine the CLI uses. Writes go through <see cref="BoardPlan"/> and nowhere else.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConfigDisplay))]
    [NotifyPropertyChangedFor(nameof(CanReload))]
    private string? _configPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyPropertyChangedFor(nameof(ShowOnboarding))]
    private string? _errorText;

    [ObservableProperty]
    private string _statusText = "No board profile open.";

    [ObservableProperty]
    private BacklogNodeViewModel? _selectedNode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentSection))]
    [NotifyPropertyChangedFor(nameof(ShowOnboarding))]
    [NotifyPropertyChangedFor(nameof(ShowBacklog))]
    [NotifyPropertyChangedFor(nameof(ShowPlan))]
    [NotifyPropertyChangedFor(nameof(ShowAudit))]
    [NotifyPropertyChangedFor(nameof(ShowPlanned))]
    private int _currentSectionIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBacklog))]
    private int _epicCount;

    [ObservableProperty]
    private int _issueCount;

    [ObservableProperty]
    private int _taskCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblems))]
    [NotifyPropertyChangedFor(nameof(MarkupSummary))]
    private int _problemCount;

    /// <summary>
    ///     True while any editor buffer differs from the file. While it is set,
    ///     the Plan gate refuses to run: a Plan is computed from the file, and
    ///     the file is the source of truth.
    /// </summary>
    [ObservableProperty]
    private bool _hasUnsavedEdits;

    [ObservableProperty]
    private string _codePrefix = string.Empty;

    [ObservableProperty]
    private string _backlogFileName = string.Empty;

    /// <summary>
    /// Which view of the description the right pane shows. The rendered preview is
    /// the default — it is how a user checks a description before it is written;
    /// the markup is there for when they need to see exactly what goes on the wire.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRenderedPreview))]
    [NotifyPropertyChangedFor(nameof(MarkupPaneTitle))]
    [NotifyPropertyChangedFor(nameof(MarkupPaneCaption))]
    private bool _showGeneratedMarkup;



    /// <summary>Settable so the Preview radio can bind two-way against it.</summary>
    public bool ShowRenderedPreview
    {
        get => !ShowGeneratedMarkup;
        set => ShowGeneratedMarkup = !value;
    }

    public string MarkupPaneTitle => ShowGeneratedMarkup ? "Generated HTML" : "Preview";

    public string MarkupPaneCaption => ShowGeneratedMarkup
        ? "What import sends, from the CLI's own converter — indented here for reading; the markup on the wire has no indentation."
        : "How this description will read on the board.";

    public ObservableCollection<BacklogNodeViewModel> Nodes { get; } = [];

    /// <summary>The first-run choice: open a profile file, or describe one here.</summary>
    public OnboardingViewModel Onboarding { get; }

    /// <summary>The Plan/Apply gate — the only path from this app to a write.</summary>
    public PlanViewModel BoardPlan { get; } = new();

    /// <summary>The read-only drift report. It authorises nothing (ABSD-304/306).</summary>
    public AuditViewModel Audit { get; } = new();

    private readonly ProfileLoader _loader;

    /// <summary>
    ///     Cancels the load in flight when another starts. A profile opened while a
    ///     large backlog is still being read must not have the first read's result
    ///     land on top of it afterwards.
    /// </summary>
    private CancellationTokenSource? _loading;

    public MainWindowViewModel(ProfileLoader loader, IBacklogFileStore store)
    {
        _loader = loader;
        Onboarding = new OnboardingViewModel(store, loader);

        // The gate reads the shell's unsaved-edits state: a Plan is computed from
        // the file, so edits that exist only in the editor buffer must not be
        // planned or applied as if they were on disk.
        BoardPlan.UnsavedEditsCheck = () => HasUnsavedEdits;

        // An audit compares the board against the file for the same reason.
        Audit.UnsavedEditsCheck = () => HasUnsavedEdits;

        // The only sanctioned route from a detected drift to a fix: Audit names the
        // command, the shell switches to the Plan surface and generates it there.
        // Nothing is pre-approved — the user still confirms, exactly as they would
        // have if they had chosen close-children themselves (ABSD-306).
        Audit.CloseChildrenRequested = () =>
        {
            BoardPlan.Choose(PlanCommand.CloseChildren);
            CurrentSectionIndex = PlanSection;
        };
    }

    /// <summary>The open profile, or null when none has been opened yet.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProfile))]
    [NotifyPropertyChangedFor(nameof(CanReload))]
    [NotifyPropertyChangedFor(nameof(ConfigDisplay))]
    [NotifyPropertyChangedFor(nameof(ShowOnboarding))]
    [NotifyPropertyChangedFor(nameof(ShowBacklog))]
    private BacklogWorkspace? _workspace;

    public bool HasProfile => Workspace is not null;

    public IReadOnlyList<NavSection> Sections { get; } =
    [
        new("Backlog", "✎", true,
            "The backlog file as parsed, beside what each item would send to the board.",
            string.Empty),
        new("Plan & Apply", "⇄", true,
            "Read the board, review every change, then write only what you confirm.",
            string.Empty),
        new("Audit", "◎", true,
            "Where the board has drifted from the backlog.",
            string.Empty),
        new("Sprints", "▤", false,
            "Which items belong to which iteration.",
            "Will assign items to the iterations named in the profile."),
        new("Assignees", "☺", false,
            "Who owns which item.",
            "Will assign items to the people named in the profile."),
        new("History", "⟲", false,
            "Every Apply this machine has run.",
            "Will list every Apply this machine has run, with its per-item outcomes."),
    ];

    public NavSection CurrentSection =>
        Sections[Math.Clamp(CurrentSectionIndex, 0, Sections.Count - 1)];

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    /// <summary>An unsaved profile has no config file, but its backlog is still on disk.</summary>
    public bool CanReload => Workspace is not null || !string.IsNullOrEmpty(ConfigPath);

    public bool HasBacklog => EpicCount > 0;

    public bool HasProblems => ProblemCount > 0;

    public string ConfigDisplay => Workspace?.OriginDisplay ?? ConfigPath ?? "No board profile open";

    // Which pane the content column shows.
    private const int BacklogSection = 0;
    private const int PlanSection = 1;
    private const int AuditSection = 2;

    // Not while an error is up — the failure banner owns the pane then.
    public bool ShowOnboarding => CurrentSectionIndex == BacklogSection && !HasProfile && !HasError;

    public bool ShowBacklog => CurrentSectionIndex == BacklogSection && HasProfile;

    public bool ShowPlan => CurrentSectionIndex == PlanSection;

    public bool ShowAudit => CurrentSectionIndex == AuditSection;

    public bool ShowPlanned => !CurrentSection.IsAvailable;

    public string MarkupSummary => ProblemCount switch
    {
        0 => "✓ Markup clean",
        1 => "! 1 markup problem",
        var n => $"! {n} markup problems",
    };

    /// <summary>Loads a Board profile, replacing whatever is currently shown.</summary>
    public async Task LoadAsync(string configPath)
    {
        var token = BeginLoad();
        StatusText = "Opening…";

        Result<BacklogWorkspace> loaded;
        try
        {
            loaded = await _loader.LoadAsync(configPath, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // A newer load owns the shell now; touching a bound property here would
            // overwrite what that load already put on screen.
            return;
        }

        if (token.IsCancellationRequested)
        {
            return;
        }

        if (loaded.IsFailure)
        {
            var error = loaded.Error!;
            Clear();
            ConfigPath = configPath;
            ErrorText = $"{error.SafeMessage} ({error.Code})";
            StatusText = "Could not open that profile.";
            return;
        }

        Adopt(loaded.Value);
    }

    /// <summary>
    ///     Opens a Board profile from the onboarding screen's "I have a
    ///     board.config.json" route. When no profile is open, a failure stays on the
    ///     first-run screen and is reported beside the route that produced it —
    ///     replacing the form with a blank error page would strand a new user.
    /// </summary>
    public async Task OpenFromOnboardingAsync(string configPath)
    {
        var token = BeginLoad();

        Result<BacklogWorkspace> loaded;
        try
        {
            loaded = await _loader.LoadAsync(configPath, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (token.IsCancellationRequested)
        {
            return;
        }

        if (loaded.IsFailure)
        {
            var error = loaded.Error!;
            Onboarding.ImportErrorText = $"{error.SafeMessage} ({error.Code})";
            return;
        }

        Adopt(loaded.Value);
    }

    /// <summary>Starts a load, cancelling whatever was still in flight.</summary>
    private CancellationToken BeginLoad()
    {
        _loading?.Cancel();
        _loading?.Dispose();
        _loading = new CancellationTokenSource();
        return _loading.Token;
    }

    /// <summary>Takes an already-opened profile, as onboarding's form route produces.</summary>
    public void Adopt(BacklogWorkspace workspace)
    {
        // The selection survives a reload or a save when the same item is still
        // there — an editor round trip must not yank the pane to another item.
        var identity = NodeIdentity.Of(SelectedNode);

        ErrorText = null;
        ConfigPath = workspace.ConfigPath;
        Workspace = workspace;
        Onboarding.ImportErrorText = null;

        // A Plan — and a drift report — belong to the profile they were computed
        // against.
        BoardPlan.Discard();
        Audit.Discard();

        // …and so does the credential badge: the sources are per-profile (the
        // keychain key, pat_env, pat_file all come from this config), so leaving
        // the previous profile's answer up would gate the wrong board (ABSD-110).
        // Not awaited: the badge is informational, and resolving it can spawn the
        // platform's credential tool. Adopt must not hold the render thread behind a
        // child process — least of all one that may be waiting on an unlock prompt.
        _ = BoardPlan.RefreshCredentialStatusAsync(workspace.Config);

        Rebuild(workspace, identity);
    }

    /// <summary>
    ///     Splices every dirty editor buffer back into the backlog text, writes the
    ///     file atomically, and re-opens the saved profile. Edits are applied
    ///     last-to-first because the parser's line ranges all refer to the original
    ///     text; splicing a later block cannot invalidate an earlier range, but the
    ///     reverse would.
    /// </summary>
    [RelayCommand]
    public async Task SaveAsync()
    {
        if (Workspace is not { } workspace || !HasUnsavedEdits)
        {
            return;
        }

        var markdown = workspace.Markdown;
        foreach (var (item, text) in CollectEdits())
        {
            markdown = BacklogSplicer.ReplaceDescription(markdown, item, text);
        }

        StatusText = "Saving…";
        var saved = await _loader.SaveAsync(workspace, markdown).ConfigureAwait(true);
        if (saved.IsFailure)
        {
            var error = saved.Error!;
            ErrorText = $"{error.SafeMessage} ({error.Code})";
            StatusText = "Could not save the backlog.";
            return;
        }

        Adopt(saved.Value);
        StatusText += " · saved";
    }

    /// <summary>The dirty buffers in document order, ready for last-to-first splicing.</summary>
    private List<(BacklogItem Item, string Text)> CollectEdits()
    {
        var edits = new List<(BacklogItem, string)>();
        Collect(Nodes);
        return edits.OrderByDescending(edit => edit.Item1.DescriptionStart).ToList();

        void Collect(IEnumerable<BacklogNodeViewModel> nodes)
        {
            foreach (var node in nodes)
            {
                if (node.IsDirty)
                {
                    edits.Add((node.Item, node.Source));
                }

                Collect(node.Children);
            }
        }
    }

    /// <summary>
    ///     Writes the import CSV from the parsed backlog as it is on disk — the same
    ///     bytes <c>gen-csv</c> writes. It needs no credential and touches no Azure
    ///     DevOps endpoint. The path comes from the shell's save-file picker.
    /// </summary>
    public async Task ExportCsvToAsync(string destinationPath)
    {
        if (Workspace is not { } workspace)
        {
            return;
        }

        var written = await _loader.ExportCsvAsync(workspace, destinationPath).ConfigureAwait(true);
        if (written.IsFailure)
        {
            var error = written.Error!;
            ErrorText = $"{error.SafeMessage} ({error.Code})";
            StatusText = "Could not write the import CSV.";
            return;
        }

        // The CSV is an artefact, not a board write, so malformed markup does not
        // block it — but the count travels with the result rather than being
        // silently dropped (ABSD-207).
        var export = written.Value;
        ErrorText = null;
        StatusText = export.MarkupProblemCount == 0
            ? $"Import CSV written to {export.Path} — {export.RowCount} row(s)."
            : $"Import CSV written to {export.Path} — {export.RowCount} row(s), "
              + $"{export.MarkupProblemCount} markup problem(s) in the backlog.";
    }

    /// <summary>True when something is already at this path, for the overwrite prompt.</summary>
    public bool FileExistsAt(string path) => _loader.Exists(path);

    /// <summary>The path the CSV save dialog offers first: the profile's own csv_file.</summary>
    public string SuggestedCsvPath => Workspace?.Config.CsvFile ?? "work-items.csv";

    /// <summary>Re-reads the current profile from disk, picking up external edits.</summary>
    public async Task ReloadAsync()
    {
        if (ConfigPath is { } path)
        {
            await LoadAsync(path).ConfigureAwait(true);
            return;
        }

        if (Workspace is not { } current)
        {
            return;
        }

        var token = BeginLoad();

        Result<BacklogWorkspace> again;
        try
        {
            again = await _loader.ReloadAsync(current, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (token.IsCancellationRequested)
        {
            return;
        }

        if (again.IsFailure)
        {
            var error = again.Error!;
            Clear();
            ErrorText = $"{error.SafeMessage} ({error.Code})";
            StatusText = "Could not reload that profile.";
            return;
        }

        Adopt(again.Value);
    }

    private void Clear()
    {
        Nodes.Clear();
        SelectedNode = null;
        Workspace = null;
        EpicCount = 0;
        IssueCount = 0;
        TaskCount = 0;
        ProblemCount = 0;
        CodePrefix = string.Empty;
        BacklogFileName = string.Empty;
        HasUnsavedEdits = false;
    }

    private void Rebuild(BacklogWorkspace workspace, NodeIdentity? preferredSelection = null)
    {
        Nodes.Clear();

        // BacklogParser returns a flat list in document order: an Epic owns every
        // Issue that follows it until the next Epic. An Issue written above the
        // first Epic is dropped upstream, exactly as the CLI drops it.
        BacklogNodeViewModel? epic = null;
        foreach (var item in workspace.Items)
        {
            var node = new BacklogNodeViewModel(item);
            if (item.Level == BacklogLevel.Epic)
            {
                epic = node;
                Nodes.Add(node);
            }
            else
            {
                epic?.Children.Add(node);
            }
        }

        HookDirtyTracking(Nodes);

        SelectedNode = FindNode(Nodes, preferredSelection)
            ?? Nodes.FirstOrDefault()?.Children.FirstOrDefault()
            ?? Nodes.FirstOrDefault();

        EpicCount = workspace.Items.Count(i => i.Level == BacklogLevel.Epic);
        IssueCount = workspace.Items.Count(i => i.Level == BacklogLevel.Issue);
        TaskCount = workspace.Items.Sum(i => i.Bullets.Count);
        ProblemCount = CountProblems(Nodes);
        CodePrefix = workspace.Config.CodePrefix;
        BacklogFileName = Path.GetFileName(workspace.BacklogPath);
        HasUnsavedEdits = false;

        StatusText =
            $"{EpicCount} epics · {IssueCount} issues · {TaskCount} tasks · " +
            $"{(ProblemCount == 0 ? "markup clean" : $"{ProblemCount} markup problems")} · " +
            $"{BacklogFileName} · prefix {CodePrefix}";
    }

    /// <summary>Dirty nodes announce themselves so the header chip and the Plan gate stay current.</summary>
    private void HookDirtyTracking(IEnumerable<BacklogNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            node.PropertyChanged += OnNodeChanged;
            HookDirtyTracking(node.Children);
        }
    }

    private void OnNodeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BacklogNodeViewModel.IsDirty))
        {
            HasUnsavedEdits = AnyDirty(Nodes);
        }
        else if (e.PropertyName == nameof(BacklogNodeViewModel.Problems))
        {
            // The header chip answers from the same live audit the tree badges do:
            // what the user is looking at is what check-html would see. While the
            // buffer is dirty, Apply is refused anyway; on save the workspace
            // re-audits the file and the two agree again.
            ProblemCount = CountProblems(Nodes);
        }
    }

    private static bool AnyDirty(IEnumerable<BacklogNodeViewModel> nodes) =>
        nodes.Any(node => node.IsDirty || AnyDirty(node.Children));

    private static int CountProblems(IEnumerable<BacklogNodeViewModel> nodes) =>
        nodes.Sum(n => n.Problems.Count + CountProblems(n.Children));

    /// <summary>Finds a node again after a rebuild, by the identity that survives a re-parse.</summary>
    private static BacklogNodeViewModel? FindNode(
        IEnumerable<BacklogNodeViewModel> nodes, NodeIdentity? wanted)
    {
        if (wanted is not { } target)
        {
            return null;
        }

        foreach (var node in nodes)
        {
            if (NodeIdentity.Of(node) == target)
            {
                return node;
            }

            var found = FindNode(node.Children, wanted);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>What a re-parse cannot change: the level, the issue code, the heading text.</summary>
    private sealed record NodeIdentity(bool IsEpic, string? Code, string Title)
    {
        public static NodeIdentity? Of(BacklogNodeViewModel? node) =>
            node is null ? null : new(node.IsEpic, node.Item.Code, node.Item.Title);
    }
}
