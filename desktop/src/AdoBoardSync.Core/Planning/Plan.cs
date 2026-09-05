using AdoBoardSync.Core.Backlog;

namespace AdoBoardSync.Core.Planning;

/// <summary>Which CLI command this Plan is the desktop equivalent of.</summary>
public enum PlanCommand
{
    /// <summary>Create Epics and Issues the board is missing. Never updates, never deletes.</summary>
    Import,

    /// <summary>Bring existing Epic/Issue titles and descriptions back in line with the backlog.</summary>
    Resync,

    /// <summary>Reconcile each Issue's child Tasks to its backlog bullets: create the missing, delete the stray.</summary>
    ResyncTasks,

    /// <summary>Delete duplicate work items, keeping the lowest id of each set.</summary>
    Dedup,

    /// <summary>Create the configured sprint iterations and set each item's iteration path.</summary>
    Sprints,

    /// <summary>Set each Issue's — and optionally its Tasks' — assignee from the config.</summary>
    Assign,

    /// <summary>Set every open descendant of a Done item to the terminal state.</summary>
    CloseChildren,

    /// <summary>Create or update exactly one Issue and set exactly one sprint on it.</summary>
    SyncOne,
}

/// <summary>
/// What a Plan row acts on. Almost everything is a work item; <c>sprints</c> also
/// creates classification nodes, which are not work items and are not created by
/// the same endpoint — so the row says which, rather than Apply guessing from the
/// command.
/// </summary>
public enum PlanTarget
{
    WorkItem,
    IterationNode,
}

/// <summary>The iteration an <see cref="PlanTarget.IterationNode"/> row would create.</summary>
public sealed record IterationSpec(string Name, string? Start, string? Finish);

/// <summary>What Apply would do to one item.</summary>
public enum PlanOperation
{
    Create,
    Update,
    Delete,
    Unchanged,
}

/// <summary>One field Apply would write, with the value it replaces.</summary>
public sealed record PlanFieldChange(string Field, string Before, string After);

/// <summary>
/// One row of a Plan: exactly one item, and exactly what would happen to it. Each
/// row carries its own glyph and label — DESIGN-SYSTEM §5.3 forbids colour alone.
///
/// Task rows carry <see cref="Level"/> set to <see cref="BacklogLevel.Issue"/> and
/// their parent Issue's code in <see cref="Code"/>; the code is what a reviewer
/// scans for, and no surface distinguishes an "issue" badge from a task's.
/// </summary>
public sealed record PlanRow
{
    public required PlanOperation Operation { get; init; }

    public required BacklogLevel Level { get; init; }

    public required string Title { get; init; }

    public string? Code { get; init; }

    /// <summary>The board id, for an item that already exists.</summary>
    public int? BoardId { get; init; }

    /// <summary>The parent Epic's board id, when it is already on the board.</summary>
    public int? ParentBoardId { get; init; }

    /// <summary>The parent Epic's title, when the parent is itself being created by this Plan.</summary>
    public string? ParentTitle { get; init; }

    public string DescriptionHtml { get; init; } = string.Empty;

    public IReadOnlyList<PlanFieldChange> Changes { get; init; } = [];

    /// <summary>What this row acts on. Work items unless the row creates a sprint node.</summary>
    public PlanTarget Target { get; init; } = PlanTarget.WorkItem;

    /// <summary>Set only on an <see cref="PlanTarget.IterationNode"/> row.</summary>
    public IterationSpec? Iteration { get; init; }

    /// <summary>
    /// The Azure DevOps work item type this row would create, when the command's
    /// level-to-type mapping is not enough to say. <c>close-children</c> and
    /// <c>assign</c> touch Tasks and Issues in one Plan, so the row carries it.
    /// </summary>
    public string? WorkItemType { get; init; }

    public string Glyph => Operation switch
    {
        PlanOperation.Create => "+",
        PlanOperation.Update => "~",
        PlanOperation.Delete => "−",
        _ => "=",
    };

    public string Label => Operation switch
    {
        PlanOperation.Create => "Create",
        PlanOperation.Update => "Update",
        PlanOperation.Delete => "Delete",
        _ => "Unchanged",
    };

    public string OperationText => $"{Glyph} {Label}";

    public bool IsCreate => Operation == PlanOperation.Create;

    public bool IsUpdate => Operation == PlanOperation.Update;

    public bool IsDelete => Operation == PlanOperation.Delete;

    public bool IsUnchanged => Operation == PlanOperation.Unchanged;

    public bool IsEpic => Level == BacklogLevel.Epic;

    /// <summary>
    /// What a reviewer scans the row by. A sprint row is neither an Epic nor an
    /// Issue — it creates a classification node — so it says so rather than
    /// borrowing the Epic badge its <see cref="Level"/> would otherwise imply.
    /// </summary>
    public string Badge => Target == PlanTarget.IterationNode
        ? "SPRINT"
        : IsEpic ? "EPIC" : Code ?? "ISSUE";

    public string BoardReference => BoardId is { } id ? $"#{id}" : "new";

    public string ChangeSummary => Changes.Count == 0
        ? string.Empty
        : string.Join(", ", Changes.Select(c => c.Field.Split('.')[^1]));
}

/// <summary>
/// An immutable, reviewed description of a set of writes. Apply consumes exactly
/// this object and performs exactly the writes its rows imply (ARCHITECTURE.md
/// §3.2). The two fingerprints are the stale-plan guard: if the backlog file or
/// the board has moved since, Apply is refused.
/// </summary>
public sealed record Plan
{
    public required PlanCommand Command { get; init; }

    public required IReadOnlyList<PlanRow> Rows { get; init; }

    /// <summary>
    /// What the Plan wants the reviewer to know that no row can say: a configured
    /// Issue code that is not on the board, a board Issue no sprint claims, a
    /// missing <c>iterations</c> array. The CLI prints these as WARN/INFO lines
    /// beside its plan; dropping them would hide the half of the answer that
    /// explains why a Plan is smaller than expected.
    /// </summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    public bool HasNotes => Notes.Count > 0;

    public required string BacklogFingerprint { get; init; }

    public required string BoardFingerprint { get; init; }

    public int CreateCount => Rows.Count(r => r.Operation == PlanOperation.Create);

    public int UpdateCount => Rows.Count(r => r.Operation == PlanOperation.Update);

    public int DeleteCount => Rows.Count(r => r.Operation == PlanOperation.Delete);

    public int UnchangedCount => Rows.Count(r => r.Operation == PlanOperation.Unchanged);

    /// <summary>Rows Apply would actually write. Unchanged rows are shown, never sent.</summary>
    public IReadOnlyList<PlanRow> WriteRows =>
        [.. Rows.Where(r => r.Operation != PlanOperation.Unchanged)];

    public bool HasWork => WriteRows.Count > 0;

    public string Summary => Command switch
    {
        PlanCommand.Import => $"{CreateCount} to create, {UnchangedCount} already on the board",
        PlanCommand.Resync => $"{UpdateCount} to update, {UnchangedCount} already in step",
        PlanCommand.Dedup when DeleteCount > 0 => $"{DeleteCount} duplicate(s) to delete",
        PlanCommand.Dedup => "no duplicates on the board",
        PlanCommand.Sprints when CreateCount > 0 || UpdateCount > 0 =>
            $"{CreateCount} iteration(s) to create, {UpdateCount} item(s) to move",
        PlanCommand.Sprints => "every item is already in its configured sprint",
        PlanCommand.Assign when UpdateCount > 0 => $"{UpdateCount} item(s) to assign",
        PlanCommand.Assign => "every item already has its configured assignee",
        PlanCommand.CloseChildren when UpdateCount > 0 =>
            $"{UpdateCount} open descendant(s) of a Done item to close",
        PlanCommand.CloseChildren => "no Done item has an open descendant",
        PlanCommand.SyncOne when CreateCount > 0 => "1 issue to create",
        PlanCommand.SyncOne when UpdateCount > 0 => $"{UpdateCount} change(s) to that issue",
        PlanCommand.SyncOne => "that issue already matches the backlog",
        _ when DeleteCount > 0 && CreateCount > 0 =>
            $"{CreateCount} task(s) to create, {DeleteCount} to delete",
        _ when DeleteCount > 0 => $"{DeleteCount} stray task(s) to delete",
        _ when CreateCount > 0 => $"{CreateCount} missing task(s) to create",
        _ => "every Task matches its backlog bullets",
    };
}
