using AdoBoardSync.Core.Planning;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdoBoardSync.Desktop.ViewModels;

/// <summary>
/// The shell's navigation half: the nav-rail sections, which pane the content
/// column shows, and the external-change banner. Split out as a partial because it
/// is one vocabulary — section names, indices, visibility — that a reader wants
/// apart from the orchestration the main file does.
/// </summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>
    ///     True once the backlog file on disk no longer matches what this profile was
    ///     opened from (ABSD-504). Set by the staleness poll and cleared only by a
    ///     reload — this is the "requires an explicit reload before continuing" half
    ///     of PRD-AC-15, the half the save-time guard cannot provide because it only
    ///     fires once the user has already done the work.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExternalChangeText))]
    private bool _isStale;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentSection))]
    [NotifyPropertyChangedFor(nameof(ShowOnboarding))]
    [NotifyPropertyChangedFor(nameof(ShowBacklog))]
    [NotifyPropertyChangedFor(nameof(ShowPlan))]
    [NotifyPropertyChangedFor(nameof(ShowAudit))]
    [NotifyPropertyChangedFor(nameof(ShowSprints))]
    [NotifyPropertyChangedFor(nameof(ShowAssignees))]
    [NotifyPropertyChangedFor(nameof(ShowHistory))]
    [NotifyPropertyChangedFor(nameof(ShowAgent))]
    [NotifyPropertyChangedFor(nameof(ShowPlanned))]
    private int _currentSectionIndex;

    public IReadOnlyList<NavSection> Sections { get; }

    public NavSection CurrentSection =>
        Sections[Math.Clamp(CurrentSectionIndex, 0, Sections.Count - 1)];

    // Which pane the content column shows.
    private const int BacklogSection = 0;
    private const int PlanSection = 1;
    private const int AuditSection = 2;
    private const int SprintsSection = 3;
    private const int AssigneesSection = 4;
    private const int HistorySection = 5;
    private const int AgentSection = 6;

    // Not while an error is up — the failure banner owns the pane then.
    public bool ShowOnboarding => CurrentSectionIndex == BacklogSection && !HasProfile && !HasError;

    public bool ShowBacklog => CurrentSectionIndex == BacklogSection && HasProfile;

    public bool ShowPlan => CurrentSectionIndex == PlanSection;

    public bool ShowAudit => CurrentSectionIndex == AuditSection;

    public bool ShowSprints => CurrentSectionIndex == SprintsSection;

    public bool ShowAssignees => CurrentSectionIndex == AssigneesSection;

    /// <summary>Guarded on the surface itself, not only on the nav entry: a
    /// keyboard shortcut can move the index without going through the rail.</summary>
    public bool ShowHistory => CurrentSectionIndex == HistorySection && History is not null;

    public bool ShowAgent => CurrentSectionIndex == AgentSection && Agent is not null;

    public bool ShowPlanned => !CurrentSection.IsAvailable;

    /// <summary>What the banner says. Empty while the profile is current.</summary>
    public string ExternalChangeText => IsStale
        ? "This backlog was changed on disk after it was opened here. Reload to see it — "
          + "planning or applying from the copy in memory would review one text and write another."
        : string.Empty;

    private static IReadOnlyList<NavSection> BuildSections(
        HistoryViewModel? history, AgentAuthoringViewModel? agent) =>
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
        new("Sprints", "▤", true,
            "Which items belong to which iteration.",
            string.Empty),
        new("Assignees", "☺", true,
            "Who owns which item.",
            string.Empty),
        new("History", "⟲", history is not null,
            "Every Apply this machine has run.",
            "Needs the operation history store, which this build was started without."),
        new("Agent", "✦", agent is not null,
            "Ask a local agent CLI to draft a change, and review it as a diff.",
            "Needs the agent edit session, which this build was started without."),
    ];
}
