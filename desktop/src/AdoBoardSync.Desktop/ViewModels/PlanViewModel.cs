using System.Collections.ObjectModel;
using System.Diagnostics;
using AdoBoardSync.Core.Board;
using AdoBoardSync.Core.Configuration;
using AdoBoardSync.Core.Diagnostics;
using AdoBoardSync.Core.Planning;
using AdoBoardSync.Core.Results;
using AdoBoardSync.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdoBoardSync.Desktop.ViewModels;

/// <summary>
///     The Plan/Apply gate: the only path from this app to a write. Generating a Plan
///     is read-only; Apply runs that Plan after an explicit confirmation, and only if
///     neither the backlog nor the board has moved since.
/// </summary>
public sealed partial class PlanViewModel : ObservableObject
{
    /// <summary>
    ///     The one credential chain, shared with Audit so the two surfaces cannot
    ///     resolve a token differently. It is the first place the OS credential
    ///     store is consulted (ABSD-103), and the badge names what answered.
    /// </summary>
    private readonly CredentialSession _credentials;

    /// <summary>
    ///     Where this gate's own events go (ABSD-507, ARCHITECTURE.md §7). The gate
    ///     is the right emitter for them because it is the only place that holds a
    ///     Plan, the duration it took, and the typed error a refusal produced — and
    ///     because every one of these events is about the operation rather than
    ///     about the store that records it.
    /// </summary>
    private readonly IDiagnostics _diagnostics;

    private readonly BoardGatewayFactory _gatewayFactory;

    /// <summary>
    ///     The local-git evidence source <c>advance</c> plans from. Resolved from the
    ///     composition root like the gateway factory; the stand-in refuses loudly
    ///     when nothing was supplied.
    /// </summary>
    private readonly IGitEvidenceSource _gitEvidence;

    /// <summary>
    ///     Records each Apply in the local history (ABSD-501). Optional: an app
    ///     with no history store still applies, it simply keeps no record — the
    ///     recorder itself already refuses to let a store failure fail a write.
    /// </summary>
    private readonly ApplyHistoryRecorder? _recorder;

    /// <summary>
    ///     The redactor every diagnostic passes through. The resolved PAT is
    ///     registered with it the moment it is resolved, which is what makes the
    ///     log safe against a token that does not look like one (ABSD-507).
    /// </summary>
    private readonly DiagnosticRedaction? _redaction;

    /// <summary>Copy a Done parent's assignee onto the items it closes. The CLI's <c>--assign-from-parent</c>.</summary>
    [ObservableProperty] private bool _assignFromParent;

    /// <summary>Skip iteration-node creation and only set paths. The CLI's <c>--assign-only</c>.</summary>
    [ObservableProperty] private bool _assignOnly;

    /// <summary>The ref <c>advance</c> counts commits against.</summary>
    [ObservableProperty] private string _baseRef = "origin/main";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsImport))]
    [NotifyPropertyChangedFor(nameof(IsResync))]
    [NotifyPropertyChangedFor(nameof(IsResyncTasks))]
    [NotifyPropertyChangedFor(nameof(SelectedCommand))]
    [NotifyPropertyChangedFor(nameof(NeedsCode))]
    [NotifyPropertyChangedFor(nameof(NeedsSprint))]
    [NotifyPropertyChangedFor(nameof(NeedsIds))]
    [NotifyPropertyChangedFor(nameof(NeedsRepos))]
    [NotifyPropertyChangedFor(nameof(HasOptions))]
    private PlanCommand _command = PlanCommand.Import;

    [ObservableProperty] private string _credentialStatus = string.Empty;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorText;

    /// <summary>Fetch each repository before probing, so the refs are not stale.</summary>
    [ObservableProperty] private bool _fetch = true;

    /// <summary>
    ///     True when a token resolved from some source. Every board-reading and
    ///     board-writing action is gated on it (PRD-AC-10); offline work — opening a
    ///     profile, the tree, the preview, markup validation, the CSV export — is not.
    /// </summary>
    [ObservableProperty] private bool _hasCredential;

    /// <summary>Cascade the change to each Issue's child Tasks. The CLI's <c>--no-tasks</c>, inverted.</summary>
    [ObservableProperty] private bool _includeTasks = true;

    [ObservableProperty] private bool _isBusy;

    /// <summary>True while the confirmation step is showing. Apply cannot run before it.</summary>
    [ObservableProperty] private bool _isConfirming;

    /// <summary>
    ///     The Issue code <c>sync-one</c> requires and <c>resync-tasks</c> may take.
    ///     Typed, not derived from the tree selection: the CLI takes it as an
    ///     argument, and a surface that silently used the selected item would apply
    ///     to something other than what the user typed.
    /// </summary>
    [ObservableProperty] private string _issueCode = string.Empty;

    /// <summary>Leave a leading <c>[ ]</c> title checkbox alone. The CLI's <c>--no-tick</c>.</summary>
    [ObservableProperty] private bool _noTick;

    /// <summary>Never overwrite an assignee somebody set. The CLI's <c>--only-unassigned</c>.</summary>
    [ObservableProperty] private bool _onlyUnassigned;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlan))]
    [NotifyPropertyChangedFor(nameof(HasWork))]
    [NotifyPropertyChangedFor(nameof(PlanSummary))]
    private Plan? _plan;

    /// <summary>Repository paths <c>advance</c> probes, comma- or newline-separated.</summary>
    [ObservableProperty] private string _repoPaths = string.Empty;

    /// <summary>Reset a failed iteration write to the project root. The CLI's <c>--reset-on-missing</c>.</summary>
    [ObservableProperty] private bool _resetOnMissing;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasToken))]
    private string _sessionToken = string.Empty;

    /// <summary>The sprint <c>sync-one</c> puts the Issue in. One of the configured iterations.</summary>
    [ObservableProperty] private string _sprintName = string.Empty;

    [ObservableProperty] private string _statusText = "No Plan generated yet.";

    /// <summary>The state <c>set-state</c> targets. Blank means the configured terminal state.</summary>
    [ObservableProperty] private string _targetState = string.Empty;

    /// <summary>Board ids <c>set-state</c> moves, typed the way the CLI takes them.</summary>
    [ObservableProperty] private string _workItemIds = string.Empty;

    public PlanViewModel(
        BoardGatewayFactory? gatewayFactory = null,
        ICredentialStore? credentialStore = null,
        ApplyHistoryRecorder? recorder = null,
        DiagnosticRedaction? redaction = null,
        IDiagnostics? diagnostics = null,
        IGitEvidenceSource? gitEvidence = null)
    {
        // Not `pat => new AzureDevOpsGateway(pat)`. The composition root registers
        // the delegate and injects it here; a default that builds a real connector
        // hides a missing registration behind a live call to somebody's board.
        _gatewayFactory = gatewayFactory
                          ?? (_ => new UnconfiguredBoardGateway("no factory was supplied to this view model"));
        _gitEvidence = gitEvidence ?? new UnconfiguredGitEvidenceSource();
        _credentials = new CredentialSession(credentialStore);
        _recorder = recorder;
        _redaction = redaction;
        _diagnostics = diagnostics ?? NullDiagnostics.Instance;

        // Both are derived from a collection, which does not raise for them. Wired
        // here rather than at each mutation site, so a third mutation cannot forget.
        Outcomes.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasOutcomes));
        Notes.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNotes));
    }

    /// <summary>
    ///     Which credential store this gate is actually using. Safe to show — the name
    ///     never carries a secret — and it is what lets a test prove the composition
    ///     root's store reached here rather than one built on the spot (ABSD-106).
    /// </summary>
    public string CredentialStoreName => _credentials.StoreName;

    /// <summary>
    ///     Set by the shell: returns true while the editor holds unsaved edits. A
    ///     Plan is computed from the backlog file, so edits that exist only in the
    ///     buffer must not be planned or applied as if they were on disk.
    /// </summary>
    public Func<bool>? UnsavedEditsCheck { get; set; }

    /// <summary>
    ///     True when the backlog file has been changed on disk since the profile was
    ///     opened. A Plan is computed from what this app last read, so planning
    ///     against a file somebody else has since rewritten would review one text and
    ///     write another (ABSD-504, PRD-AC-15).
    /// </summary>
    public Func<bool>? StaleProfileCheck { get; set; }


    public ObservableCollection<PlanRow> Rows { get; } = [];

    public ObservableCollection<ApplyOutcome> Outcomes { get; } = [];

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    public bool HasOutcomes => Outcomes.Count > 0;

    public bool HasPlan => Plan is not null;

    public bool HasWork => Plan?.HasWork == true;

    public bool HasToken => !string.IsNullOrWhiteSpace(SessionToken);

    public bool IsImport => Command == PlanCommand.Import;

    public bool IsResync => Command == PlanCommand.Resync;

    public bool IsResyncTasks => Command == PlanCommand.ResyncTasks;


    /// <summary>
    ///     Settable so the selector list can bind two-way against it. Assigning it
    ///     goes through the same <c>SetCommand</c> every other route uses, so
    ///     choosing from the list discards the previous command's Plan exactly as
    ///     the keyboard route does.
    /// </summary>
    public PlanCommandOption SelectedCommand
    {
        get => Commands.FirstOrDefault(c => c.Command == Command) ?? Commands[0];
        set => SetCommand(value.Command);
    }

    public bool NeedsCode => SelectedCommand.NeedsCode;

    public bool NeedsSprint => SelectedCommand.NeedsSprint;

    public bool NeedsIds => SelectedCommand.NeedsIds;

    public bool NeedsRepos => SelectedCommand.NeedsRepos;

    public IReadOnlyList<PlanCommandOption> Commands { get; } = PlanCommandCatalog.All;

    public bool HasOptions => SelectedCommand.HasOptions;

    /// <summary>Notes the Plan Builder attached — a misconfiguration, or a code it could not place.</summary>
    public ObservableCollection<string> Notes { get; } = [];

    public bool HasNotes => Notes.Count > 0;

    public string PlanSummary => Plan?.Summary ?? string.Empty;

    /// <summary>Picks a command. The selector binds <see cref="SelectedCommand" /> instead.</summary>
    public void Choose(PlanCommand command)
    {
        SetCommand(command);
    }

    private void SetCommand(PlanCommand command)
    {
        if (Command == command) return;

        Command = command;

        // A Plan belongs to the command that produced it.
        Discard();
    }

    /// <summary>Drops the current Plan and any confirmation in progress.</summary>
    public void Discard()
    {
        Plan = null;
        Rows.Clear();
        Outcomes.Clear();
        Notes.Clear();
        IsConfirming = false;
        StatusText = "No Plan generated yet.";
    }

    /// <summary>Reads the board and computes the diff. Issues no write.</summary>
    public async Task GenerateAsync(BacklogWorkspace workspace, CancellationToken cancellationToken = default)
    {
        if (BlockedByUnsavedEdits("generating a Plan")) return;

        // Typed inputs are checked before the credential is resolved, matching the
        // CLI: argparse refuses a command line with no ids or no repository before
        // any authentication happens.
        if (Command == PlanCommand.SetState && ParsedWorkItemIds().Count == 0)
        {
            ErrorText = "Type at least one work item id — the board numbers, separated by "
                        + "commas or spaces. (setstate.no_ids)";
            StatusText = "No work item ids to plan.";
            return;
        }

        if (Command == PlanCommand.Advance && ParsedRepoPaths().Count == 0)
        {
            ErrorText = "Type at least one repository path to probe. (advance.no_repo)";
            StatusText = "No repositories to probe.";
            return;
        }

        var token = await ResolveTokenAsync(workspace.Config, cancellationToken).ConfigureAwait(true);
        if (token is null)
        {
            ErrorText = CredentialStatus;
            return;
        }

        IsBusy = true;
        ErrorText = null;
        IsConfirming = false;
        Outcomes.Clear();
        StatusText = "Reading the board…";

        var startedGenerating = Stopwatch.GetTimestamp();

        try
        {
            var gateway = _gatewayFactory(token);
            try
            {
                var snapshot = await gateway.ReadAsync(workspace.Config, cancellationToken);
                if (snapshot.IsFailure)
                {
                    ErrorText = $"{snapshot.Error!.SafeMessage} ({snapshot.Error.Code})";
                    StatusText = "Could not read the board.";
                    _diagnostics.OperationFailed("plan", snapshot.Error);
                    return;
                }

                GitProbeReport? evidence = null;
                if (Command == PlanCommand.Advance)
                {
                    var probed = await _gitEvidence.ProbeAsync(
                        ParsedRepoPaths(), BaseRef.Trim(), Fetch,
                        workspace.Config.IssueCodeRegex.ToString(), cancellationToken);

                    if (probed.IsFailure)
                    {
                        ErrorText = $"{probed.Error!.SafeMessage} ({probed.Error.Code})";
                        StatusText = "Could not probe the repositories.";
                        _diagnostics.OperationFailed("plan", probed.Error);
                        return;
                    }

                    evidence = probed.Value;
                }

                var built = Build(workspace, snapshot.Value, evidence);
                if (built.IsFailure)
                {
                    ErrorText = $"{built.Error!.SafeMessage} ({built.Error.Code})";
                    StatusText = "Could not build that Plan.";
                    _diagnostics.OperationFailed("plan", built.Error);
                    return;
                }

                var plan = built.Value;
                _diagnostics.PlanGenerated(plan, Stopwatch.GetElapsedTime(startedGenerating));
                Plan = plan;
                Rows.Clear();
                foreach (var row in plan.Rows) Rows.Add(row);

                Notes.Clear();
                foreach (var note in plan.Notes) Notes.Add(note);

                StatusText = plan.HasWork
                    ? plan.Summary
                    : $"Nothing to do — {plan.Summary}.";
            }
            finally
            {
                (gateway as IDisposable)?.Dispose();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    ///     Dispatches to the Plan Builder for the selected command. Every branch is a
    ///     pure call over the backlog and the snapshot — no branch reaches a write
    ///     port, which is what makes "generating a Plan writes nothing" a property of
    ///     the code rather than a habit.
    /// </summary>
    private Result<Plan> Build(BacklogWorkspace workspace, BoardSnapshot snapshot, GitProbeReport? evidence = null)
    {
        var config = workspace.Config;
        var items = workspace.Items;
        var markdown = workspace.Markdown;

        return Command switch
        {
            PlanCommand.Import => PlanBuilder.BuildImport(config, items, snapshot, markdown),
            PlanCommand.Resync => PlanBuilder.BuildResync(config, items, snapshot, markdown),
            PlanCommand.ResyncTasks => PlanBuilder.BuildResyncTasks(config, items, snapshot, markdown),
            PlanCommand.Sync => PlanBuilder.BuildSync(config, items, snapshot, markdown),
            PlanCommand.Dedup => PlanBuilder.BuildDedup(config, snapshot, markdown),
            PlanCommand.Sprints => PlanBuilder.BuildSprints(
                config, snapshot, markdown, AssignOnly, IncludeTasks, ResetOnMissing),
            PlanCommand.Assign => PlanBuilder.BuildAssign(
                config, snapshot, markdown, IncludeTasks, OnlyUnassigned),
            PlanCommand.CloseChildren => PlanBuilder.BuildCloseChildren(
                config, snapshot, markdown, AssignFromParent),
            PlanCommand.SetState => PlanBuilder.BuildSetState(
                config, snapshot, markdown, ParsedWorkItemIds(), TargetState, NoTick),
            PlanCommand.Advance when evidence is { } probe => PlanBuilder.BuildAdvance(
                config, items, snapshot, markdown, probe, BaseRef.Trim()),
            PlanCommand.Advance => Error.SourceFailure(
                "advance.not_probed",
                "The repositories were not probed, so there is no evidence to plan from."),
            _ => PlanBuilder.BuildSyncOne(config, items, snapshot, markdown, IssueCode, SprintName)
        };
    }

    /// <summary>The board ids typed for <c>set-state</c>, in any mix of commas, spaces and newlines.</summary>
    private IReadOnlyList<int> ParsedWorkItemIds()
    {
        var ids = new List<int>();
        foreach (var token in WorkItemIds.Split(
                     [',', ';', ' ', '\n', '\t'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (int.TryParse(token, out var id))
                ids.Add(id);

        return ids;
    }

    /// <summary>The repository paths typed for <c>advance</c>, in any mix of commas, semicolons and newlines.</summary>
    private IReadOnlyList<string> ParsedRepoPaths()
    {
        var paths = RepoPaths.Split(
            [',', ';', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return paths.Where(p => p.Length > 0).ToArray();
    }

    /// <summary>
    ///     Resolves the token and publishes the badge. Each source is read exactly
    ///     once, off the calling thread; the status names the winning source and any
    ///     source that failed, never the value it held.
    /// </summary>
    private async Task<string?> ResolveTokenAsync(BoardConfig config, CancellationToken cancellationToken)
    {
        var (resolver, resolution) = await _credentials
            .ResolveAsync(config, SessionToken, cancellationToken).ConfigureAwait(true);

        CredentialStatus = CredentialSession.Describe(resolver, resolution);
        HasCredential = resolution.Found;

        // Registering the resolved token is what makes the diagnostics log safe
        // against a PAT that does not match any recognisable shape. The shape
        // backstop in DiagnosticRedaction is the fallback, not the guarantee —
        // this is the guarantee, and it has to happen here because this is the
        // only place in the application that ever holds the value.
        if (resolution.Token is { Length: > 0 } token) _redaction?.Register(token);

        return resolution.Token;
    }

    /// <summary>
    ///     Recomputes the badge for a profile without generating anything. Called when
    ///     the active profile changes, so the badge describes the profile on screen
    ///     rather than the last one a Plan was built for (ABSD-110).
    /// </summary>
    public async Task RefreshCredentialStatusAsync(
        BoardConfig? config, CancellationToken cancellationToken = default)
    {
        if (config is null)
        {
            CredentialStatus = string.Empty;
            HasCredential = false;
            return;
        }

        await ResolveTokenAsync(config, cancellationToken).ConfigureAwait(true);
    }
}