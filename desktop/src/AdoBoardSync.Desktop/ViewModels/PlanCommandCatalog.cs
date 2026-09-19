using AdoBoardSync.Core.Planning;

namespace AdoBoardSync.Desktop.ViewModels;

/// <summary>
///     One entry in the command selector. FSD §3.3 requires the surface to state each
///     command's scope and whether <c>sync</c> runs it, because those two facts are
///     what a user needs before choosing — "will this touch my whole board?" and "does
///     the everyday reconcile already do this for me?".
///     The per-command options are declared here rather than branched on in the view,
///     so a toggle cannot be shown for a command that ignores it.
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
    bool SupportsAssignFromParent = false,
    bool SupportsResetOnMissing = false,
    bool NeedsIds = false,
    bool SupportsTargetState = false,
    bool SupportsNoTick = false,
    bool NeedsRepos = false)
{
    /// <summary>Shown beside the name, so the sync chain is legible without the docs.</summary>
    public string ChainNote => InSyncChain ? "part of sync" : "run on its own";

    public bool HasOptions =>
        SupportsIncludeTasks || SupportsAssignOnly || SupportsOnlyUnassigned
        || SupportsAssignFromParent || SupportsResetOnMissing || SupportsNoTick;
}

/// <summary>
///     The command catalog, stated once: every command the Plan surface offers, with
///     the scope and sync-chain flag FSD §3.3 requires beside each. Audit is not here:
///     it writes nothing and has its own read-only section (ABSD-306), so putting it
///     behind a Plan/Apply gate would imply it could write.
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
        new(PlanCommand.Import, "Import", "Epics and Issues missing from the board", true),
        new(PlanCommand.Resync, "Resync", "Titles and descriptions of every Epic and Issue", true),
        new(PlanCommand.ResyncTasks, "Resync tasks", "Each Issue's child Tasks, or one Issue's",
            true, true),
        new(PlanCommand.Sync, "Sync", "Import, then resync, then the task reconcile, in one review",
            false),
        new(PlanCommand.Dedup, "Dedup", "Every duplicate work item on the board", false),
        new(PlanCommand.Sprints, "Sprints", "The configured iterations and the items in them",
            false, SupportsIncludeTasks: true, SupportsAssignOnly: true,
            SupportsResetOnMissing: true),
        new(PlanCommand.Assign, "Assign", "Each Issue's owner, from the profile's assignees",
            false, SupportsIncludeTasks: true, SupportsOnlyUnassigned: true),
        new(PlanCommand.CloseChildren, "Close children", "Open descendants of anything already Done",
            false, SupportsAssignFromParent: true),
        new(PlanCommand.SyncOne, "Sync one", "Exactly one Issue, and one sprint for it",
            false, true, true),
        new(PlanCommand.SetState, "Set state", "Work items named by id, moved straight to a target state",
            false, NeedsIds: true, SupportsTargetState: true, SupportsNoTick: true),
        new(PlanCommand.Advance, "Advance", "Issues with commit evidence on local branches: start state to working",
            false, NeedsRepos: true)
    ];
}