using System.Collections.ObjectModel;
using AdoBoardSync.Core.Backlog;
using AdoBoardSync.Core.Configuration;
using AdoBoardSync.Core.Planning;
using AdoBoardSync.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdoBoardSync.Desktop.ViewModels;

/// <summary>
///     The shell view model: one Board profile at a time, parsed with the same Core
///     engine the CLI uses. Writes go through <see cref="BoardPlan" /> and nowhere else.
///     It is orchestration only, by design. The tree is a
///     <see cref="BacklogTreeViewModel" />, the profile's life between file and shell
///     is a <see cref="Services.ProfileSession" />, and every write to the board lives
///     behind <see cref="PlanViewModel" /> — so what remains here is the wiring between
///     surfaces and the state the header, the footer and the pane switches show.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly ProfileSession _session;

    [ObservableProperty] private string _backlogFileName = string.Empty;

    [ObservableProperty] private string _codePrefix = string.Empty;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ConfigDisplay))] [NotifyPropertyChangedFor(nameof(CanReload))]
    private string? _configPath;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasError))] [NotifyPropertyChangedFor(nameof(ShowOnboarding))]
    private string? _errorText;

    /// <summary>
    ///     Which view of the description the right pane shows. The rendered preview is
    ///     the default — it is how a user checks a description before it is written;
    ///     the markup is there for when they need to see exactly what goes on the wire.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRenderedPreview))]
    [NotifyPropertyChangedFor(nameof(MarkupPaneTitle))]
    [NotifyPropertyChangedFor(nameof(MarkupPaneCaption))]
    private bool _showGeneratedMarkup;

    [ObservableProperty] private string _statusText = "No board profile open.";

    /// <summary>The open profile, or null when none has been opened yet.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProfile))]
    [NotifyPropertyChangedFor(nameof(CanReload))]
    [NotifyPropertyChangedFor(nameof(ConfigDisplay))]
    [NotifyPropertyChangedFor(nameof(ShowOnboarding))]
    [NotifyPropertyChangedFor(nameof(ShowBacklog))]
    private BacklogWorkspace? _workspace;

    /// <param name="surfaces">
    ///     The panes the shell hosts. Injected rather than
    ///     constructed here so the composition root's instances — the Plan gate carrying
    ///     the history recorder and the diagnostics redactor among them — are the ones
    ///     the shell actually shows. Building the gate inline was how Apply came to
    ///     record nothing outside the tests (ABSD-501). Omitted in a test that is not
    ///     about a surface, which then gets stand-alone defaults.
    /// </param>
    public MainWindowViewModel(
        ProfileLoader loader,
        IBacklogFileStore store,
        ShellSurfaces? surfaces = null)
    {
        Onboarding = new OnboardingViewModel(store, loader);

        surfaces ??= ShellSurfaces.StandAlone();
        BoardPlan = surfaces.Plan;
        Audit = surfaces.Audit;
        Sprints = surfaces.Sprints;
        Assignees = surfaces.Assignees;
        History = surfaces.History;
        Profiles = surfaces.Profiles;
        Agent = surfaces.Agent;

        Sections = BuildSections(History, Agent);

        _session = new ProfileSession(
            loader,
            Adopt,
            HandleOpenFailed,
            HandleOnboardingOpenFailed,
            HandleReloadFailed,
            HandleStaleDetected);

        // Saving an iteration or an assignee rewrites board.config.json, so the
        // profile the rest of the shell is showing is now stale. The table re-reads
        // it and hands the fresh workspace back here, rather than each table
        // holding its own divergent copy.
        Sprints.Reloaded = Adopt;
        Assignees.Reloaded = Adopt;

        // Choosing another profile in the switcher opens it here. The switcher owns
        // which profile is active; the shell owns what is on screen, and this is the
        // one edge between them.
        if (Profiles is not null) Profiles.ActiveProfileChanged += OnActiveProfileChangedAsync;

        if (Agent is not null)
        {
            // An accepted edit changed the backlog file underneath us. Re-opening
            // is what makes the tree, the preview and the Plan describe the file
            // that is now on disk rather than the one the agent started from.
            Agent.EditAccepted = _ => ReloadAfterAgentEdit();

            // The same handoff the Audit surface has, and for the same reason: an
            // agent's involvement removes no step from the Plan/Apply gate. This
            // opens the Plan surface and nothing more — no plan, no approval
            // (ABSD-705).
            Agent.PlanRequested = () =>
            {
                BoardPlan.Choose(PlanCommand.Import);
                CurrentSectionIndex = PlanSection;
            };
        }

        // The gate reads the tree's unsaved-edits state: a Plan is computed from
        // the file, so edits that exist only in the editor buffer must not be
        // planned or applied as if they were on disk.
        BoardPlan.UnsavedEditsCheck = () => Tree.HasUnsavedEdits;

        // An audit compares the board against the file for the same reason.
        Audit.UnsavedEditsCheck = () => Tree.HasUnsavedEdits;

        // …and both are equally wrong against a file somebody else has rewritten
        // since it was opened (ABSD-504).
        BoardPlan.StaleProfileCheck = () => IsStale;

        // The only sanctioned route from a detected drift to a fix: Audit names the
        // command, the shell switches to the Plan surface and generates it there.
        // Nothing is pre-approved — the user still confirms, exactly as they would
        // have if they had chosen close-children themselves (ABSD-306).
        Audit.CloseChildrenRequested = () =>
        {
            BoardPlan.Choose(PlanCommand.CloseChildren);
            CurrentSectionIndex = PlanSection;
        };

        // The agent prompt follows the rail's selection; the pass-through re-raises
        // so anything bound to the shell's SelectedNode keeps working too.
        Tree.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BacklogTreeViewModel.SelectedNode)) Agent?.ScopeTo(Tree.SelectedNode?.Item);
        };
    }

    /// <summary>The tree, the editor buffers, and the counts the chips show.</summary>
    public BacklogTreeViewModel Tree { get; } = new();

    // Read-only pass-throughs, for tests and for the shell's own status line. The
    // views bind Tree.* directly — a pass-through property cannot raise on behalf
    // of the tree, so a binding here would freeze on its first value.

    public ObservableCollection<BacklogNodeViewModel> Nodes => Tree.Nodes;

    public BacklogNodeViewModel? SelectedNode
    {
        get => Tree.SelectedNode;
        set => Tree.SelectedNode = value;
    }

    public int EpicCount => Tree.EpicCount;

    public int IssueCount => Tree.IssueCount;

    public int TaskCount => Tree.TaskCount;

    public bool HasUnsavedEdits => Tree.HasUnsavedEdits;

    public int ProblemCount => Tree.ProblemCount;

    public bool HasProblems => Tree.HasProblems;

    public bool HasBacklog => Tree.HasBacklog;

    public string MarkupSummary => Tree.MarkupSummary;

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

    /// <summary>The first-run choice: open a profile file, or describe one here.</summary>
    public OnboardingViewModel Onboarding { get; }

    /// <summary>The Plan/Apply gate — the only path from this app to a write.</summary>
    public PlanViewModel BoardPlan { get; }

    /// <summary>The read-only drift report. It authorises nothing (ABSD-304/306).</summary>
    public AuditViewModel Audit { get; }

    /// <summary>The iteration table, edited into the profile's own config (ABSD-401).</summary>
    public SprintPlanningViewModel Sprints { get; }

    /// <summary>The assignee table, edited into the profile's own config (ABSD-402).</summary>
    public AssigneePlanningViewModel Assignees { get; }

    /// <summary>
    ///     Every Apply this machine has run against the open profile (ABSD-508).
    ///     Null when no history store was supplied — the section then reports
    ///     itself unavailable rather than opening an empty timeline that looks
    ///     like "you have never applied anything".
    /// </summary>
    public HistoryViewModel? History { get; }

    /// <summary>The known board profiles, and which one is open (ABSD-502).</summary>
    public ProfileRegistryViewModel? Profiles { get; }

    /// <summary>
    ///     The agent-authoring surface (ABSD-703 through ABSD-706). Null when no
    ///     agent session was supplied, and the section then reports itself
    ///     unavailable rather than offering to run a binary this build cannot find.
    /// </summary>
    public AgentAuthoringViewModel? Agent { get; }

    public bool HasProfile => Workspace is not null;

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    /// <summary>An unsaved profile has no config file, but its backlog is still on disk.</summary>
    public bool CanReload => Workspace is not null || !string.IsNullOrEmpty(ConfigPath);

    public string ConfigDisplay => Workspace?.OriginDisplay ?? ConfigPath ?? "No board profile open";

    /// <summary>The path the CSV save dialog offers first: the profile's own csv_file.</summary>
    public string SuggestedCsvPath => Workspace?.Config.CsvFile ?? "work-items.csv";

    /// <summary>
    ///     Re-opens the profile after an agent's edit was accepted. Not awaited —
    ///     the accept has already returned to the surface that asked for it — but
    ///     named rather than discarded inline, so the fire-and-forget is deliberate
    ///     and visible.
    /// </summary>
    private void ReloadAfterAgentEdit()
    {
        _ = ReloadAsync();
    }

    /// <summary>
    ///     Keeps the agent's scope on whatever the backlog rail has selected, so the
    ///     prompt says "the selected Issue" about the Issue the user is looking at.
    /// </summary>
    public Task CheckForExternalChangeAsync()
    {
        return _session.CheckForExternalChangeAsync(Workspace, IsStale);
    }

    /// <summary>Whether two config paths name the same file. Stated on <see cref="ProfileEntry" />.</summary>
    private static bool SamePath(string? left, string? right)
    {
        return ProfileEntry.SamePath(left, right);
    }

    /// <summary>Loads a Board profile, replacing whatever is currently shown.</summary>
    public Task LoadAsync(string configPath)
    {
        return _session.LoadAsync(configPath);
    }

    /// <summary>Opens a Board profile from the onboarding screen's config-file route.</summary>
    public Task OpenFromOnboardingAsync(string configPath)
    {
        return _session.OpenFromOnboardingAsync(configPath);
    }

    private void HandleOpenFailed(string configPath, string message)
    {
        Clear();
        ConfigPath = configPath;
        ErrorText = message;
        StatusText = "Could not open that profile.";
    }

    private void HandleOnboardingOpenFailed(string message)
    {
        Onboarding.ImportErrorText = message;
    }

    private void HandleReloadFailed(string message)
    {
        Clear();
        ErrorText = message;
        StatusText = "Could not reload that profile.";
    }

    private void HandleStaleDetected()
    {
        IsStale = true;
        StatusText = "The backlog file changed on disk. Reload to pick it up.";
    }

    /// <summary>Takes an already-opened profile, as onboarding's form route produces.</summary>
    public void Adopt(BacklogWorkspace workspace)
    {
        // The selection survives a reload or a save when the same item is still
        // there — an editor round trip must not yank the pane to another item.
        var identity = Tree.SelectionIdentity;

        ErrorText = null;
        ConfigPath = workspace.ConfigPath;
        Workspace = workspace;
        Onboarding.ImportErrorText = null;

        // Whatever the file said a moment ago, this workspace was read from it now.
        // Clearing here rather than in the reload path covers every route back to a
        // current profile — reload, save, an accepted agent edit, a profile switch.
        IsStale = false;

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

        // The two config tables read from the profile that is now open. Synchronous
        // because both are reading the config already in memory — neither touches a
        // disk or a board to fill itself in.
        Sprints.Load(workspace);
        Assignees.Load(workspace);

        // The timeline is per-profile, so adopting a different one has to re-scope
        // it. Not awaited, for the same reason the credential badge is not: a SQLite
        // read must not hold the render thread while the backlog is being built.
        // It takes the load token so that opening a second profile abandons the
        // first one's timeline read rather than letting it land on top.
        _ = History?.LoadAsync(workspace, _session.LoadToken);

        // Registering the profile is what makes it reappear in the switcher next
        // time (ABSD-502). A profile with no config file on disk is skipped by the
        // registry itself — there would be nothing to reopen. Explicitly
        // uncancellable: this one writes a file, and a half-written registry is
        // worse than a slow one.
        if (Profiles is { } profiles) _ = profiles.AddSafelyAsync(workspace, CancellationToken.None);

        if (Agent is { } agent)
        {
            // The agent runs in this profile's directory and edits its backlog, so
            // it must be pointed at the profile now open. The previous run's diff
            // and verdict go with it: a review left standing would offer to accept
            // an edit to a file this shell is no longer showing.
            agent.Discard();
            agent.Workspace = workspace;
        }

        Tree.Rebuild(workspace, identity);
        CodePrefix = workspace.Config.CodePrefix;
        BacklogFileName = Path.GetFileName(workspace.BacklogPath);

        var (epics, issues, tasks) = Tree.Counts;
        StatusText =
            $"{epics} epics · {issues} issues · {tasks} tasks · " +
            $"{(Tree.HasProblems ? $"{Tree.ProblemCount} markup problems" : "markup clean")} · " +
            $"{BacklogFileName} · prefix {CodePrefix}";
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
        if (Workspace is not { } workspace || !Tree.HasUnsavedEdits) return;

        var markdown = workspace.Markdown;
        foreach (var (item, text) in Tree.CollectEdits())
            markdown = BacklogSplicer.ReplaceDescription(markdown, item, text);

        StatusText = "Saving…";
        var saved = await _session.SaveAsync(workspace, markdown).ConfigureAwait(true);
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

    /// <summary>
    ///     Writes the import CSV from the parsed backlog as it is on disk — the same
    ///     bytes <c>gen-csv</c> writes. It needs no credential and touches no Azure
    ///     DevOps endpoint. The path comes from the shell's save-file picker.
    /// </summary>
    public async Task ExportCsvToAsync(string destinationPath)
    {
        if (Workspace is not { } workspace) return;

        var written = await _session.ExportCsvAsync(workspace, destinationPath).ConfigureAwait(true);
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
    public bool FileExistsAt(string path)
    {
        return _session.Exists(path);
    }

    /// <summary>Re-reads the current profile from disk, picking up external edits.</summary>
    public async Task ReloadAsync()
    {
        if (ConfigPath is { } path)
        {
            await _session.LoadAsync(path).ConfigureAwait(true);
            return;
        }

        if (Workspace is not { } current) return;

        await _session.ReloadAsync(current).ConfigureAwait(true);
    }

    /// <summary>
    ///     Opens whichever profile the switcher just made active.
    ///     The path comparison is what stops the cycle: <see cref="Adopt" /> registers
    ///     the profile it opened, which is what raises this in the first place.
    ///     Re-opening a profile that is already on screen would re-enter Adopt and
    ///     discard the Plan the user was looking at.
    /// </summary>
    private Task OnActiveProfileChangedAsync(ProfileEntry? profile, CancellationToken cancellationToken)
    {
        if (profile is null || SamePath(profile.ConfigPath, ConfigPath)) return Task.CompletedTask;

        return LoadAsync(profile.ConfigPath);
    }

    private void Clear()
    {
        Tree.Clear();
        Workspace = null;
        CodePrefix = string.Empty;
        BacklogFileName = string.Empty;

        // The per-profile surfaces go with it. A timeline or an iteration table
        // left standing after the profile it described was closed is the same
        // failure as a stale Plan, and Clear runs on exactly the paths — a failed
        // open, a failed reload — where the previous profile is gone for good.
        History?.Clear();
        Sprints.Clear();
        Assignees.Clear();
    }
}