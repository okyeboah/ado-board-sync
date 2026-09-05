using System.Collections.ObjectModel;
using AdoBoardSync.Core.Agents;
using AdoBoardSync.Core.Backlog;
using AdoBoardSync.Core.Diff;
using AdoBoardSync.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdoBoardSync.Desktop.ViewModels;

/// <summary>What a prompt applies to, in the words the surface offers it.</summary>
public sealed record AgentScopeOption(AgentScope Scope, string Name);

/// <summary>
///     The agent-authoring surface (ABSD-703 through ABSD-706): a prompt scoped to
///     the selected Epic or Issue or to the whole backlog, the agent's edit reviewed
///     as a diff, and every run recorded.
///
///     The three disclosure lines are properties rather than tooltip text on purpose.
///     A user about to hand a local CLI a directory needs to read which binary will
///     run, what it can see, and what it may change while they are deciding — not
///     after hovering something. They restate themselves as the provider and the
///     scope change, so the sentence on screen always describes the run that is about
///     to happen.
///
///     Accepting an edit changes a file and nothing else. The board consequences are
///     a Plan, and <see cref="PlanRequested" /> only asks the shell to switch to that
///     surface — it approves nothing, exactly as the Audit handoff does not
///     (ABSD-705).
/// </summary>
public sealed partial class AgentAuthoringViewModel : ObservableObject
{
    private readonly AgentEditSession _session;

    private readonly IAgentProviderRegistry _registry;

    private CancellationTokenSource? _running;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun))]
    [NotifyPropertyChangedFor(nameof(ReadStatement))]
    [NotifyPropertyChangedFor(nameof(ChangeStatement))]
    private BacklogWorkspace? _workspace;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun))]
    [NotifyPropertyChangedFor(nameof(HasProvider))]
    [NotifyPropertyChangedFor(nameof(ProviderStatement))]
    private InstalledAgent? _selectedProvider;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun))]
    private string _prompt = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScopeStatement))]
    private AgentScope _scope = AgentScope.Backlog;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScopeStatement))]
    private string? _scopeLabel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun))]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorText;

    [ObservableProperty]
    private string _statusText = "No agent has been run against this backlog yet.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun))]
    [NotifyPropertyChangedFor(nameof(HasReview))]
    [NotifyPropertyChangedFor(nameof(DiffSummary))]
    private AgentEditProposal? _proposal;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPlan))]
    private bool _hasAcceptedEdit;

    public AgentAuthoringViewModel(AgentEditSession session, IAgentProviderRegistry registry)
    {
        _session = session;
        _registry = registry;
    }

    /// <summary>The agent CLIs found on this machine, in the order discovery offers them.</summary>
    public ObservableCollection<InstalledAgent> Providers { get; } = [];

    /// <summary>The agent's own output, streamed while it runs.</summary>
    public ObservableCollection<string> Output { get; } = [];

    /// <summary>The edit under review, line by line. Empty until there is one.</summary>
    public ObservableCollection<DiffLine> DiffLines { get; } = [];

    public IReadOnlyList<AgentScopeOption> Scopes { get; } =
    [
        new(AgentScope.Backlog, "The whole backlog"),
        new(AgentScope.Epic, "The selected Epic"),
        new(AgentScope.Issue, "The selected Issue"),
    ];

    /// <summary>Raised once an edit has been accepted, with the parse it was validated by.</summary>
    public Action<AgentEditReview>? EditAccepted { get; set; }

    /// <summary>
    ///     Asks the shell to open the Plan surface. Named as a request because that is
    ///     all it is: the Plan is read from the board and confirmed there like any
    ///     other, and an agent's involvement removes no step from that gate.
    /// </summary>
    public Action? PlanRequested { get; set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    public bool HasProvider => SelectedProvider is not null;

    public bool HasReview => Proposal?.IsUnderReview == true;

    public bool CanCancel => IsRunning;

    public bool CanRun =>
        Workspace is not null
        && SelectedProvider is not null
        && !string.IsNullOrWhiteSpace(Prompt)
        && !IsRunning
        && !HasReview;

    /// <summary>Offered only after an edit was accepted — there is nothing new to plan otherwise.</summary>
    public bool CanPlan => HasAcceptedEdit;

    public string DiffSummary => Proposal?.Review?.Diff is { } diff
        ? diff.IsCoarse
            ? $"{diff.Summary} (shown as a whole-block replacement — the change was too large to match line by line)"
            : diff.Summary
        : string.Empty;

    /// <summary>Which binary will run. First of the three things stated before the run.</summary>
    public string ProviderStatement => SelectedProvider is { } agent
        ? $"{agent.Provider.DisplayName} {agent.Version} runs from {agent.ExecutablePath}. "
          + "It signs in with its own credentials; this app holds none of them."
        : "No agent CLI was found on this machine. Install Claude Code, Codex CLI, OpenCode or Gemini CLI, then look again.";

    /// <summary>What it can read. Second.</summary>
    public string ReadStatement => Workspace is { } workspace
        ? $"It runs in {workspace.Config.BaseDirectory} and can read the files there — this profile and its "
          + "backlog. Your Azure DevOps token is removed from its environment before it starts."
        : "It runs in the open profile's directory and can read the files there.";

    /// <summary>What it may change. Third, and the one that is easiest to get wrong.</summary>
    public string ChangeStatement => Workspace is { } workspace
        ? $"It may change {workspace.BacklogPath}. Nothing it writes is kept until you accept the diff, and "
          + "nothing reaches Azure DevOps: every board change still goes through Plan and Apply."
        : "It may change the backlog file only. Nothing reaches Azure DevOps without a Plan and a confirmation.";

    public string ScopeStatement => Scope switch
    {
        AgentScope.Epic => $"Scoped to the Epic \"{ScopeLabel}\" and the Issues under it.",
        AgentScope.Issue => $"Scoped to the Issue {ScopeLabel}.",
        _ => "Scoped to the whole backlog.",
    };

    /// <summary>Points the prompt at one parsed item, as the tree selection changes.</summary>
    public void ScopeTo(BacklogItem? item)
    {
        if (item is null)
        {
            ScopeToBacklog();
            return;
        }

        Scope = item.Level == BacklogLevel.Epic ? AgentScope.Epic : AgentScope.Issue;
        ScopeLabel = item.Level == BacklogLevel.Epic ? item.Title : item.Code;
    }

    public void ScopeToBacklog()
    {
        Scope = AgentScope.Backlog;
        ScopeLabel = null;
    }

    public void Choose(AgentScopeOption option)
    {
        Scope = option.Scope;
        if (option.Scope == AgentScope.Backlog)
        {
            ScopeLabel = null;
        }
    }

    /// <summary>Asks which agent CLIs are installed, and selects the first.</summary>
    public async Task DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var found = await _registry.DiscoverAsync(cancellationToken).ConfigureAwait(true);
        if (found.IsFailure)
        {
            ErrorText = $"{found.Error!.SafeMessage} ({found.Error.Code})";
            return;
        }

        Providers.Clear();
        foreach (var agent in found.Value)
        {
            Providers.Add(agent);
        }

        SelectedProvider = Providers.FirstOrDefault();
        StatusText = Providers.Count switch
        {
            0 => "No agent CLI found on this machine.",
            1 => "1 agent CLI found.",
            var n => $"{n} agent CLIs found.",
        };
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (!CanRun || Workspace is not { } workspace || SelectedProvider is not { } agent)
        {
            return;
        }

        Discard();
        IsRunning = true;
        StatusText = $"{agent.Provider.DisplayName} is running…";

        _running?.Dispose();
        _running = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            var progress = new Progress<string>(line => Output.Add(line));
            var run = await _session.RunAsync(
                new AgentEditRequest
                {
                    Workspace = workspace,
                    Agent = agent,
                    Prompt = Prompt,
                    Scope = Scope,
                    ScopeLabel = ScopeLabel,
                },
                progress,
                _running.Token).ConfigureAwait(true);

            if (run.IsFailure)
            {
                ErrorText = $"{run.Error!.SafeMessage} ({run.Error.Code})";
                StatusText = "The agent was not run.";
                return;
            }

            Adopt(run.Value);
        }
        finally
        {
            IsRunning = false;
        }
    }

    /// <summary>
    ///     Stops the run. The agent's own edit is undone by the session, not here: a
    ///     cancelled run leaves a half-written backlog, and half of an edit is not
    ///     something to offer as a diff.
    /// </summary>
    public void Cancel()
    {
        _running?.Cancel();
        StatusText = "Cancelling…";
    }

    /// <summary>Keeps the edit, and hands the parse it was validated by to the shell.</summary>
    public async Task AcceptAsync(CancellationToken cancellationToken = default)
    {
        if (Proposal is not { } proposal || proposal.Review is not { } review)
        {
            return;
        }

        var accepted = await _session.AcceptAsync(proposal, cancellationToken).ConfigureAwait(true);
        if (accepted.IsFailure)
        {
            // The file already holds the edit, so the user keeps it; only the record
            // of the verdict is missing, and saying so is better than implying the
            // accept failed.
            ErrorText = $"{accepted.Error!.SafeMessage} ({accepted.Error.Code})";
        }

        HasAcceptedEdit = true;
        StatusText = $"Edit accepted ({review.Diff.Summary}). Generate a Plan to see what it means for the board.";
        Proposal = null;
        DiffLines.Clear();

        EditAccepted?.Invoke(review);
    }

    /// <summary>Puts the file back byte for byte and records the verdict.</summary>
    public async Task RejectAsync(CancellationToken cancellationToken = default)
    {
        if (Proposal is not { } proposal || proposal.Review is null)
        {
            return;
        }

        var rejected = await _session.RejectAsync(proposal, cancellationToken).ConfigureAwait(true);
        if (rejected.IsFailure)
        {
            ErrorText = $"{rejected.Error!.SafeMessage} ({rejected.Error.Code})";
            StatusText = "The backlog could not be put back as it was.";
            return;
        }

        StatusText = "Edit rejected. The backlog is exactly as it was before the run.";
        Proposal = null;
        DiffLines.Clear();
    }

    public void RequestPlan()
    {
        if (!CanPlan)
        {
            return;
        }

        PlanRequested?.Invoke();
    }

    /// <summary>Clears the previous run, so a second one cannot be read as the first.</summary>
    public void Discard()
    {
        ErrorText = null;
        Output.Clear();
        DiffLines.Clear();
        Proposal = null;
        HasAcceptedEdit = false;
    }

    private void Adopt(AgentEditProposal proposal)
    {
        Proposal = proposal;
        StatusText = proposal.Summary;

        if (proposal.Refusal is { } refusal)
        {
            ErrorText = $"{refusal.SafeMessage} ({refusal.Code})";
        }

        if (proposal.RestoreError is { } restore)
        {
            ErrorText = $"{restore.SafeMessage} ({restore.Code})";
        }

        if (proposal.HistoryError is { } history)
        {
            // Not fatal to the review, but it must not pass unsaid: without the row,
            // this run is not in the history the record exists to provide.
            ErrorText = $"The run was not recorded: {history.SafeMessage} ({history.Code})";
        }

        if (proposal.Review is not { } review)
        {
            return;
        }

        foreach (var line in review.Diff.Lines)
        {
            DiffLines.Add(line);
        }
    }
}
