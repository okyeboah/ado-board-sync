using System.Collections.ObjectModel;
using AdoBoardSync.Core.Board;
using AdoBoardSync.Core.Configuration;
using AdoBoardSync.Core.Diagnostics;
using AdoBoardSync.Core.Planning;
using AdoBoardSync.Core.Results;
using AdoBoardSync.Desktop.Services;
using AdoBoardSync.Infrastructure;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdoBoardSync.Desktop.ViewModels;

/// <summary>
/// One entry in the command selector. FSD §3.3 requires the surface to state each
/// command's scope and whether <c>sync</c> runs it, because those two facts are
/// what a user needs before choosing — "will this touch my whole board?" and "does
/// the everyday reconcile already do this for me?".
///
/// The per-command options are declared here rather than branched on in the view,
/// so a toggle cannot be shown for a command that ignores it.
/// </summary>
public sealed record PlanCommandOption(
    PlanCommand Command,
    string Name,
    string Scope,
    bool InSyncChain,
    bool NeedsCode = false,
    bool NeedsSprint = false,
    bool SupportsIncludeTasks = false,
    bool SupportsAssignOnly = false,
    bool SupportsOnlyUnassigned = false,
    bool SupportsAssignFromParent = false)
{
    /// <summary>Shown beside the name, so the sync chain is legible without the docs.</summary>
    public string ChainNote => InSyncChain ? "part of sync" : "run on its own";

    public bool HasOptions =>
        SupportsIncludeTasks || SupportsAssignOnly || SupportsOnlyUnassigned || SupportsAssignFromParent;
}

/// <summary>
///     The Plan/Apply gate: the only path from this app to a write. Generating a Plan
///     is read-only; Apply runs that Plan after an explicit confirmation, and only if
///     neither the backlog nor the board has moved since.
/// </summary>
public sealed partial class PlanViewModel : ObservableObject
{
    private readonly Func<string, IBoardGateway> _gatewayFactory;

    /// <summary>
    ///     The operating system's credential store, when this machine has one. It is
    ///     the first source <see cref="PatResolver" /> checks (ABSD-103), and the one
    ///     the badge names when it answers.
    /// </summary>
    private readonly ICredentialStore _credentialStore;

    /// <summary>
    ///     Set by the shell: returns true while the editor holds unsaved edits. A
    ///     Plan is computed from the backlog file, so edits that exist only in the
    ///     buffer must not be planned or applied as if they were on disk.
    /// </summary>
    public Func<bool>? UnsavedEditsCheck { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsImport))]
    [NotifyPropertyChangedFor(nameof(IsResync))]
    [NotifyPropertyChangedFor(nameof(IsResyncTasks))]
    [NotifyPropertyChangedFor(nameof(SelectedCommand))]
    [NotifyPropertyChangedFor(nameof(NeedsCode))]
    [NotifyPropertyChangedFor(nameof(NeedsSprint))]
    [NotifyPropertyChangedFor(nameof(HasOptions))]
    private PlanCommand _command = PlanCommand.Import;

    /// <summary>
    ///     The Issue code <c>sync-one</c> requires and <c>resync-tasks</c> may take.
    ///     Typed, not derived from the tree selection: the CLI takes it as an
    ///     argument, and a surface that silently used the selected item would apply
    ///     to something other than what the user typed.
    /// </summary>
    [ObservableProperty] private string _issueCode = string.Empty;

    /// <summary>The sprint <c>sync-one</c> puts the Issue in. One of the configured iterations.</summary>
    [ObservableProperty] private string _sprintName = string.Empty;

    /// <summary>Cascade the change to each Issue's child Tasks. The CLI's <c>--no-tasks</c>, inverted.</summary>
    [ObservableProperty] private bool _includeTasks = true;

    /// <summary>Skip iteration-node creation and only set paths. The CLI's <c>--assign-only</c>.</summary>
    [ObservableProperty] private bool _assignOnly;

    /// <summary>Never overwrite an assignee somebody set. The CLI's <c>--only-unassigned</c>.</summary>
    [ObservableProperty] private bool _onlyUnassigned;

    /// <summary>Copy a Done parent's assignee onto the items it closes. The CLI's <c>--assign-from-parent</c>.</summary>
    [ObservableProperty] private bool _assignFromParent;

    [ObservableProperty] private string _credentialStatus = string.Empty;

    /// <summary>
    ///     True when a token resolved from some source. Every board-reading and
    ///     board-writing action is gated on it (PRD-AC-10); offline work — opening a
    ///     profile, the tree, the preview, markup validation, the CSV export — is not.
    /// </summary>
    [ObservableProperty] private bool _hasCredential;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorText;

    [ObservableProperty] private bool _isBusy;

    /// <summary>True while the confirmation step is showing. Apply cannot run before it.</summary>
    [ObservableProperty] private bool _isConfirming;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlan))]
    [NotifyPropertyChangedFor(nameof(HasWork))]
    [NotifyPropertyChangedFor(nameof(PlanSummary))]
    private Plan? _plan;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasToken))]
    private string _sessionToken = string.Empty;

    [ObservableProperty] private string _statusText = "No Plan generated yet.";

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

    public PlanViewModel(
        Func<string, IBoardGateway>? gatewayFactory = null,
        ICredentialStore? credentialStore = null,
        ApplyHistoryRecorder? recorder = null,
        DiagnosticRedaction? redaction = null)
    {
        _gatewayFactory = gatewayFactory ?? (pat => new AzureDevOpsGateway(pat));
        // Not OsCredentialStore.ForThisPlatform(). The composition root registers
        // the platform's store and injects it here; this fallback is for a view
        // model built outside the container, and it must be the *empty* store
        // rather than the real one. Reaching for the keychain from a default
        // constructor meant a missing registration still worked, and a test that
        // should have used no store quietly read the developer's own secrets —
        // the failure would only ever have shown up as a prompt nobody expected.
        _credentialStore = credentialStore
            ?? new UnavailableCredentialStore("no credential store was supplied to this view model");
        _recorder = recorder;
        _redaction = redaction;

        // Both are derived from a collection, which does not raise for them. Wired
        // here rather than at each mutation site, so a third mutation cannot forget.
        Outcomes.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasOutcomes));
        Notes.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNotes));
    }

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
    ///     Every command the surface offers, with the scope and sync-chain flag FSD
    ///     §3.3 requires beside each. Audit is not here: it writes nothing and has
    ///     its own read-only section (ABSD-306), so putting it behind a Plan/Apply
    ///     gate would imply it could write.
    /// </summary>
    public IReadOnlyList<PlanCommandOption> Commands { get; } =
    [
        new(PlanCommand.Import, "Import", "Epics and Issues missing from the board", InSyncChain: true),
        new(PlanCommand.Resync, "Resync", "Titles and descriptions of every Epic and Issue", InSyncChain: true),
        new(PlanCommand.ResyncTasks, "Resync tasks", "Each Issue's child Tasks, or one Issue's",
            InSyncChain: true, NeedsCode: true),
        new(PlanCommand.Dedup, "Dedup", "Every duplicate work item on the board", InSyncChain: false),
        new(PlanCommand.Sprints, "Sprints", "The configured iterations and the items in them",
            InSyncChain: false, SupportsIncludeTasks: true, SupportsAssignOnly: true),
        new(PlanCommand.Assign, "Assign", "Each Issue's owner, from the profile's assignees",
            InSyncChain: false, SupportsIncludeTasks: true, SupportsOnlyUnassigned: true),
        new(PlanCommand.CloseChildren, "Close children", "Open descendants of anything already Done",
            InSyncChain: false, SupportsAssignFromParent: true),
        new(PlanCommand.SyncOne, "Sync one", "Exactly one Issue, and one sprint for it",
            InSyncChain: false, NeedsCode: true, NeedsSprint: true),
    ];

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

    public bool HasOptions => SelectedCommand.HasOptions;

    /// <summary>Notes the Plan Builder attached — a misconfiguration, or a code it could not place.</summary>
    public ObservableCollection<string> Notes { get; } = [];

    public bool HasNotes => Notes.Count > 0;

    public string PlanSummary => Plan?.Summary ?? string.Empty;

    public string ConfirmQuestion => Plan is null
        ? string.Empty
        : Command switch
        {
            PlanCommand.Import =>
                $"Create {Plan.CreateCount} work item{(Plan.CreateCount == 1 ? string.Empty : "s")} in Azure DevOps?",
            PlanCommand.Resync =>
                $"Update {Plan.UpdateCount} work item{(Plan.UpdateCount == 1 ? string.Empty : "s")} in Azure DevOps?",
            _ => ConfirmTasksQuestion(Plan)
        };

    private static string ConfirmTasksQuestion(Plan plan)
    {
        var parts = new List<string>();
        if (plan.CreateCount > 0)
            parts.Add($"create {plan.CreateCount} task{(plan.CreateCount == 1 ? string.Empty : "s")}");

        if (plan.DeleteCount > 0) parts.Add($"delete {plan.DeleteCount}");

        return parts.Count > 0
            ? char.ToUpperInvariant(parts[0][0]) + parts[0][1..]
                                                 + (parts.Count > 1 ? " and " + parts[1] : string.Empty)
                                                 + " in Azure DevOps?"
            : "No Task changes to apply.";
    }

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
        if (BlockedByUnsavedEdits("generating a Plan"))
        {
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
                    return;
                }

                var built = Build(workspace, snapshot.Value);
                if (built.IsFailure)
                {
                    ErrorText = $"{built.Error!.SafeMessage} ({built.Error.Code})";
                    StatusText = "Could not build that Plan.";
                    return;
                }

                var plan = built.Value;
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
    private Result<Plan> Build(BacklogWorkspace workspace, BoardSnapshot snapshot)
    {
        var config = workspace.Config;
        var items = workspace.Items;
        var markdown = workspace.Markdown;

        return Command switch
        {
            PlanCommand.Import => PlanBuilder.BuildImport(config, items, snapshot, markdown),
            PlanCommand.Resync => PlanBuilder.BuildResync(config, items, snapshot, markdown),
            PlanCommand.ResyncTasks => PlanBuilder.BuildResyncTasks(config, items, snapshot, markdown),
            PlanCommand.Dedup => PlanBuilder.BuildDedup(config, snapshot, markdown),
            PlanCommand.Sprints => PlanBuilder.BuildSprints(
                config, snapshot, markdown, AssignOnly, IncludeTasks),
            PlanCommand.Assign => PlanBuilder.BuildAssign(
                config, snapshot, markdown, IncludeTasks, OnlyUnassigned),
            PlanCommand.CloseChildren => PlanBuilder.BuildCloseChildren(
                config, snapshot, markdown, AssignFromParent),
            _ => PlanBuilder.BuildSyncOne(config, items, snapshot, markdown, IssueCode, SprintName),
        };
    }

    /// <summary>
    ///     Opens the confirmation step. It never writes anything itself.
    ///     PRD-AC-03: malformed backlog markup blocks Apply before a confirmation is
    ///     ever offered. The workspace carries the offline audit total, so the gate
    ///     reads the same number the tree badges and the problems card show.
    /// </summary>
    public void RequestApply(BacklogWorkspace? workspace)
    {
        if (Plan?.HasWork != true) return;

        if (workspace is { MarkupProblemCount: > 0 } blocked)
        {
            ErrorText = blocked.MarkupProblemCount == 1
                ? "The backlog has 1 markup problem. Fix it — check-html would fail too — then generate the Plan again. (markup.invalid)"
                : $"The backlog has {blocked.MarkupProblemCount} markup problems. Fix them — check-html would fail too — then generate the Plan again. (markup.invalid)";
            StatusText = "Apply is blocked until the markup problems are fixed.";
            IsConfirming = false;
            return;
        }

        IsConfirming = true;
    }

    public void CancelApply()
    {
        IsConfirming = false;
    }

    /// <summary>
    ///     Executes the confirmed Plan. The fresh board read is the staleness check
    ///     only — the rows applied are the reviewed ones, never recomputed from it.
    ///     The markup gate runs again here: the confirmation dialog is not the only
    ///     line of defence, so removing one still leaves the other.
    /// </summary>
    public async Task ApplyConfirmedAsync(BacklogWorkspace workspace, CancellationToken cancellationToken = default)
    {
        if (Plan is not { } plan || !IsConfirming) return;

        if (BlockedByUnsavedEdits("applying"))
        {
            return;
        }

        if (workspace.MarkupProblemCount > 0)
        {
            ErrorText =
                $"The backlog has {workspace.MarkupProblemCount} markup problem(s). Fix them before applying. (markup.invalid)";
            StatusText = "Apply refused.";
            IsConfirming = false;
            return;
        }

        var token = await ResolveTokenAsync(workspace.Config, cancellationToken).ConfigureAwait(true);
        if (token is null)
        {
            ErrorText = CredentialStatus;
            return;
        }

        IsBusy = true;
        IsConfirming = false;
        ErrorText = null;
        Outcomes.Clear();
        StatusText = "Applying…";

        try
        {
            var gateway = _gatewayFactory(token);
            try
            {
                var fresh = await gateway.ReadAsync(workspace.Config, cancellationToken);
                if (fresh.IsFailure)
                {
                    ErrorText = $"{fresh.Error!.SafeMessage} ({fresh.Error.Code})";
                    StatusText = "Could not verify the board before applying.";
                    return;
                }

                var currentBacklog = PlanBuilder.FingerprintBacklog(workspace.Markdown);

                // The run is opened before the first write and closed after the
                // last, so an app that dies mid-Apply leaves an open run — which
                // is the honest record of what happened, and the one a user comes
                // to the History view looking for.
                var startedAt = DateTimeOffset.UtcNow;
                if (_recorder is { } recorder)
                {
                    await recorder.BeginAsync(
                        workspace.ProfileKey, plan.Command, startedAt, cancellationToken);
                }

                // Two observers, because they need opposite things. The list is
                // bound, so it must be touched on the UI thread, which is what
                // Progress<T> is for. The recorder must not be: Progress<T> posts
                // to the dispatcher and returns, so a callback that started the
                // history write would not have run yet when the run is closed
                // below — and a completed run refuses outcomes, silently dropping
                // the rows PRD-AC-08 promises. Recording therefore happens inline
                // on the thread that reported the outcome, which puts the write on
                // the recorder's chain before ApplyAsync returns.
                var ui = new Progress<ApplyOutcome>(Outcomes.Add);
                var progress = new RecordingProgress(ui, _recorder, cancellationToken);

                var report = await ApplyExecutor.ApplyAsync(
                    gateway, workspace.Config, plan,
                    currentBacklog, fresh.Value.Fingerprint,
                    progress, cancellationToken);

                if (report.IsFailure)
                {
                    ErrorText = $"{report.Error!.SafeMessage} ({report.Error.Code})";
                    StatusText = "Apply refused.";

                    // Refused before the first write, so there is no run to close.
                    // Abandoning leaves the opened row unfinished rather than
                    // claiming a clean end to something that never ran.
                    _recorder?.Abandon();

                    // The approved Plan no longer describes the board.
                    Plan = null;
                    Rows.Clear();
                    return;
                }

                if (_recorder is { } closing)
                {
                    await closing.CompleteAsync(
                        report.Value.Summary, DateTimeOffset.UtcNow, cancellationToken);
                }

                StatusText = report.Value.Summary;

                // The board has moved; a further write needs a fresh Plan.
                Plan = null;
                Rows.Clear();
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
    ///     The unsaved-edits half of the gate. Reports the refusal and returns true
    ///     when the editor holds work the file does not — the same file every Plan
    ///     and every Apply fingerprint is computed from.
    /// </summary>
    private bool BlockedByUnsavedEdits(string action)
    {
        if (UnsavedEditsCheck?.Invoke() != true)
        {
            return false;
        }

        ErrorText =
            "The backlog has unsaved edits. Save them first — a Plan is computed from "
            + "the file, and the file is the source of truth. (backlog.unsaved)";
        StatusText = $"Save the backlog before {action}.";
        return true;
    }

    /// <summary>
    ///     The sources this profile's token can come from, in order: one typed this
    ///     session, then the operating system's credential store, then the CLI's own
    ///     environment variable and token file.
    /// </summary>
    private PatResolver ResolverFor(BoardConfig config)
    {
        var sources = new List<IPatSource>();
        if (!string.IsNullOrWhiteSpace(SessionToken))
        {
            sources.Add(new SessionPatSource(SessionToken));
        }

        sources.AddRange(PatResolver.ForConfig(config, _credentialStore).Sources);
        return new PatResolver(sources);
    }

    /// <summary>
    ///     Resolves the token and describes the attempt. Each source is read exactly
    ///     once; the status names the winning source without re-reading anything, and
    ///     never the value it held.
    /// </summary>
    private string? ResolveToken(BoardConfig config)
    {
        var resolver = ResolverFor(config);
        return Adopt(resolver, resolver.ResolveDetailed());
    }

    /// <summary>
    ///     The same resolution, off the calling thread. Reading the operating
    ///     system's credential store spawns a child process — and on a locked
    ///     keychain that child blocks on an unlock prompt for up to its timeout — so
    ///     on the render thread it freezes the window, which is the very hazard
    ///     <see cref="ProfileLoader" /> moves the file read off that thread to avoid.
    /// </summary>
    private async Task<string?> ResolveTokenAsync(BoardConfig config, CancellationToken cancellationToken)
    {
        var resolver = ResolverFor(config);
        var resolution = await Task.Run(resolver.ResolveDetailed, cancellationToken).ConfigureAwait(true);
        return Adopt(resolver, resolution);
    }

    /// <summary>Publishes one resolution to the bound state. The only writer of both.</summary>
    private string? Adopt(PatResolver resolver, PatResolution resolution)
    {
        CredentialStatus = Describe(resolver, resolution);
        HasCredential = resolution.Found;

        // Registering the resolved token is what makes the diagnostics log safe
        // against a PAT that does not match any recognisable shape. The shape
        // backstop in DiagnosticRedaction is the fallback, not the guarantee —
        // this is the guarantee, and it has to happen here because this is the
        // only place in the application that ever holds the value.
        if (resolution.Token is { Length: > 0 } token)
        {
            _redaction?.Register(token);
        }

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

    /// <summary>
    ///     What the badge says. A source that failed is named separately from one that
    ///     simply held nothing: "checked and empty" and "checked and refused" call for
    ///     different fixes, and collapsing them sends the user to the wrong one.
    /// </summary>
    private static string Describe(PatResolver resolver, PatResolution resolution)
    {
        var trouble = resolution.HasFailures
            ? " " + string.Join(" ", resolution.Failures.Select(f => $"{f.SafeMessage} ({f.Code})"))
            : string.Empty;

        return resolution.Found
            ? $"Token resolved from {resolution.SourceName}.{trouble}"
            : $"No personal access token found. Checked {resolver.DescribeSources()}.{trouble}";
    }

    /// <summary>
    ///     Puts each outcome on the history recorder's queue immediately, then hands
    ///     it to the bound collection through the dispatcher.
    ///
    ///     The order matters and the synchrony matters. Apply reports an outcome from
    ///     whichever worker finished the write, and closes the run as soon as the last
    ///     one returns. Anything that deferred the recording — a Progress&lt;T&gt;, a
    ///     Task.Run — would still be waiting to start when the run closed, and the
    ///     store refuses outcomes for a completed run: the rows would be dropped with
    ///     nothing but a diagnostics line to say so.
    /// </summary>
    private sealed class RecordingProgress(
        IProgress<ApplyOutcome> ui, ApplyHistoryRecorder? recorder, CancellationToken cancellationToken)
        : IProgress<ApplyOutcome>
    {
        public void Report(ApplyOutcome value)
        {
            // Fire-and-forget, but already queued: the recorder chains this write
            // internally and CompleteAsync awaits that chain. Recording never paces
            // the board writes, and it swallows its own failures rather than
            // turning a support problem into a failed Apply.
            _ = recorder?.RecordAsync(value, DateTimeOffset.UtcNow, cancellationToken);

            ui.Report(value);
        }
    }
}
