using AdoBoardSync.Core.Results;
using AdoBoardSync.Desktop.Services;

namespace AdoBoardSync.Desktop.ViewModels;

/// <summary>
/// One entry in the nav rail. A section that is not built yet says so in its own
/// words rather than opening an empty screen.
/// </summary>
public sealed record NavSection(string Name, string Glyph, bool IsAvailable, string Caption, string PlannedDetail);

/// <summary>
/// The panes the shell hosts, gathered into one argument so the composition root
/// hands the shell its surfaces rather than the shell building them.
///
/// <see cref="History" /> and <see cref="Profiles" /> are nullable because they are
/// the two that need a store: a build with no operation history has no timeline to
/// show, and says so, rather than showing an empty one.
/// </summary>
/// <param name="Plan">The Plan/Apply gate — the only path from this app to a write.</param>
public sealed record ShellSurfaces(
    PlanViewModel Plan,
    AuditViewModel Audit,
    SprintPlanningViewModel Sprints,
    AssigneePlanningViewModel Assignees,
    HistoryViewModel? History = null,
    ProfileRegistryViewModel? Profiles = null,
    AgentAuthoringViewModel? Agent = null)
{
    /// <summary>
    /// Surfaces with no injected collaborators, for a test whose subject is
    /// elsewhere. The two store-backed panes are absent by construction: a default
    /// that reached for the real SQLite file would put a test's writes in the
    /// user's own history. The tables get the stand-alone reload refusal — they
    /// never open a workspace here, so it is unreachable, and if it were reached
    /// it would name itself instead of quietly building a second loader.
    /// </summary>
    public static ShellSurfaces StandAlone() => new(
        new PlanViewModel(),
        new AuditViewModel(),
        new SprintPlanningViewModel(reload: StandAloneReloads.Refusal),
        new AssigneePlanningViewModel(reload: StandAloneReloads.Refusal));
}
