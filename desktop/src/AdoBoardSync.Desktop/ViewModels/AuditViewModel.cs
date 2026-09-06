using System.Collections.ObjectModel;
using AdoBoardSync.Core.Board;
using AdoBoardSync.Core.Configuration;
using AdoBoardSync.Core.Planning;
using AdoBoardSync.Desktop.Services;
using AdoBoardSync.Infrastructure;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdoBoardSync.Desktop.ViewModels;

/// <summary>
///     The Audit surface (ABSD-304): where the board has drifted from the backlog.
///
///     It is read-only by construction and not merely by convention — it holds no
///     gateway write call at all, and <see cref="AuditReport" /> is not something
///     Apply can consume. Acting on a finding means generating the Plan that fixes
///     it, which goes through the same confirmation gate as every other write. That
///     is the whole point of keeping audit out of the Plan/Apply object graph: a
///     surface that reports drift must not be able to also correct it silently.
///
///     The handoff to close-children (ABSD-306) is therefore a <em>request</em>: it
///     names the command the shell should switch to, and the shell generates that
///     Plan from scratch. Nothing here pre-approves it.
/// </summary>
public sealed partial class AuditViewModel : ObservableObject
{
    private readonly Func<string, IBoardGateway> _gatewayFactory;
    private readonly ICredentialStore _credentialStore;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorText;

    [ObservableProperty] private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReport))]
    [NotifyPropertyChangedFor(nameof(IsClean))]
    [NotifyPropertyChangedFor(nameof(ResultText))]
    [NotifyPropertyChangedFor(nameof(HeaderLines))]
    [NotifyPropertyChangedFor(nameof(CanCloseChildren))]
    private AuditReport? _report;

    [ObservableProperty] private string _sessionToken = string.Empty;

    [ObservableProperty] private string _statusText = "The board has not been audited yet.";

    [ObservableProperty] private string _credentialStatus = string.Empty;

    public AuditViewModel(
        Func<string, IBoardGateway>? gatewayFactory = null,
        ICredentialStore? credentialStore = null)
    {
        // Refuses rather than building a real connector — see PlanViewModel. The
        // container binds the delegate; a view model built outside it has no board.
        _gatewayFactory = gatewayFactory ?? NoGatewayConfigured;
        // The empty store, not the platform's — see PlanViewModel. The composition
        // root injects the real one; a view model built outside it must not reach
        // into the user's keychain on its own (ABSD-106).
        _credentialStore = credentialStore
            ?? new UnavailableCredentialStore("no credential store was supplied to this view model");
    }

    /// <summary>See <see cref="PlanViewModel" />: a default that silently reaches a
    /// live board is not a seam.</summary>
    private static IBoardGateway NoGatewayConfigured(string personalAccessToken) =>
        throw new InvalidOperationException(
            "No board gateway factory was supplied to this AuditViewModel. Resolve it from "
            + "AppServices rather than constructing the view model directly.");

    /// <summary>Every difference, in the order the report produced them.</summary>
    public ObservableCollection<AuditFinding> Findings { get; } = [];

    /// <summary>
    ///     Parents whose children are all Done. Held apart from the findings because
    ///     the CLI prints these but does not exit 1 on them: folding them in would
    ///     make a clean board read as dirty.
    /// </summary>
    public ObservableCollection<AuditFinding> Reviews { get; } = [];

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    public bool HasReport => Report is not null;

    public bool IsClean => Report?.IsClean == true;

    public bool HasReviews => Reviews.Count > 0;

    /// <summary>
    ///     The handoff is offered only when close-children would actually do
    ///     something. An enabled button that plans nothing teaches a user to
    ///     distrust the button.
    /// </summary>
    public bool CanCloseChildren => Report?.OpenDescendantsOfDone.Count > 0;

    public string CloseChildrenCaption => Report is null
        ? string.Empty
        : Report.OpenDescendantsOfDone.Sum(f => f.BoardIds.Count) is var open && open == 1
            ? "Plan the close of 1 open descendant"
            : $"Plan the close of {open} open descendants";

    /// <summary>The CLI's own result line, in the UI's words.</summary>
    public string ResultText => Report is null
        ? string.Empty
        : Report.Summary;

    /// <summary>
    ///     The counts the CLI prints above its result. They are worth showing even
    ///     on a clean board: "checked 12 issues against backlog bullets" is what
    ///     makes a PASS mean something rather than looking like a no-op.
    /// </summary>
    public IReadOnlyList<string> HeaderLines => Report is null
        ? []
        :
        [
            $"Epics: board {Report.BoardEpicCount} / backlog {Report.BacklogEpicCount}",
            $"Issues: board {Report.BoardIssueCount} / backlog {Report.BacklogIssueCount}",
            $"Task parity: {Report.IssuesTaskChecked} issue(s) checked against backlog bullets",
            $"Duplicates: {Report.Count(AuditKind.Duplicate)} code(s) or title(s) claimed twice",
        ];

    /// <summary>
    ///     Set by the shell. Raised when the user asks to act on the open-descendant
    ///     findings; the shell switches to the Plan surface and generates a
    ///     close-children Plan there. This view model never writes.
    /// </summary>
    public Action? CloseChildrenRequested { get; set; }

    /// <summary>
    ///     Set by the shell: true while the editor holds unsaved edits. Audit reads
    ///     the backlog file, so a report computed while the buffer differs from the
    ///     file would describe a backlog nobody has.
    /// </summary>
    public Func<bool>? UnsavedEditsCheck { get; set; }

    /// <summary>Drops the current report. A report belongs to the profile it was computed against.</summary>
    public void Discard()
    {
        Report = null;
        Findings.Clear();
        Reviews.Clear();
        ErrorText = null;
        StatusText = "The board has not been audited yet.";
    }

    /// <summary>
    ///     Reads the board and compares it to the backlog. Issues no write, and
    ///     needs no confirmation for the same reason.
    /// </summary>
    public async Task RunAsync(BacklogWorkspace workspace, CancellationToken cancellationToken = default)
    {
        if (UnsavedEditsCheck?.Invoke() == true)
        {
            ErrorText =
                "The backlog has unsaved edits. Save them first — an audit compares the board "
                + "against the file, and the file is the source of truth. (backlog.unsaved)";
            StatusText = "Save the backlog before auditing.";
            return;
        }

        var token = ResolveToken(workspace.Config);
        if (token is null)
        {
            ErrorText = CredentialStatus;
            StatusText = "An audit reads the board, so it needs a token.";
            return;
        }

        IsBusy = true;
        ErrorText = null;
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

                var report = PlanBuilder.BuildAudit(
                    workspace.Config, workspace.Items, snapshot.Value, workspace.Markdown);

                Report = report;

                Findings.Clear();
                foreach (var finding in report.Findings)
                {
                    Findings.Add(finding);
                }

                Reviews.Clear();
                foreach (var review in report.Reviews)
                {
                    Reviews.Add(review);
                }

                OnPropertyChanged(nameof(HasReviews));
                OnPropertyChanged(nameof(CloseChildrenCaption));

                StatusText = report.Summary;
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

    /// <summary>Asks the shell to plan the close-children run. Plans nothing itself.</summary>
    public void RequestCloseChildren()
    {
        if (CanCloseChildren)
        {
            CloseChildrenRequested?.Invoke();
        }
    }

    /// <summary>
    ///     Resolves the token the same way the Plan gate does: one typed this
    ///     session first, then the OS credential store, then the CLI's environment
    ///     variable and token file. The status names the winning source, never the
    ///     value.
    /// </summary>
    private string? ResolveToken(BoardConfig config)
    {
        // The shared chain, with a token typed this session in front of it. Built
        // through PatResolver.ForConfig rather than assembled here so the Audit
        // surface cannot drift from the Plan gate's order — two surfaces resolving
        // credentials differently is exactly the bug a user cannot diagnose.
        var shared = PatResolver.ForConfig(config, _credentialStore);
        var sources = string.IsNullOrWhiteSpace(SessionToken)
            ? shared.Sources
            : [new SessionPatSource(SessionToken), .. shared.Sources];

        var resolver = new PatResolver(sources);
        var resolution = resolver.ResolveDetailed();

        CredentialStatus = resolution.Found
            ? $"Token resolved from {resolution.SourceName}."
            : $"No personal access token found. Checked {resolver.DescribeSources()}.";

        return resolution.Token;
    }
}
