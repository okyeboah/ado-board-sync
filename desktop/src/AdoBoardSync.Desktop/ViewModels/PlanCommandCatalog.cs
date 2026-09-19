using AdoBoardSync.Core.Planning;

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
/// The command catalog, stated once: every command the Plan surface offers, with
/// the scope and sync-chain flag FSD §3.3 requires beside each. Audit is not here:
/// it writes nothing and has its own read-only section (ABSD-306), so putting it
/// behind a Plan/Apply gate would imply it could write.
/// </summary>
public static class PlanCommandCatalog
{
    /// <summary>
    ///     Every command the surface offers, with the scope and sync-chain flag FSD
    ///     §3.3 requires beside each. Audit is not here: it writes nothing and has
    ///     its own read-only section (ABSD-306), so putting it behind a Plan/Apply
    ///     gate would imply it could write.
    /// </summary>
    public static IReadOnlyList<PlanCommandOption> All { get; } =
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
    ];}
