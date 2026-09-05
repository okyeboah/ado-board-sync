using AdoBoardSync.Core.Backlog;

namespace AdoBoardSync.Core.Planning;

/// <summary>
/// The kinds of drift <c>audit</c> reports. Each maps to exactly one check in the
/// CLI's <c>commands.audit</c>, so a finding here and a line there mean the same
/// thing.
/// </summary>
public enum AuditKind
{
    /// <summary>In the backlog, not on the board.</summary>
    Missing,

    /// <summary>
    /// On the board, with nothing in the backlog that names it — the CLI's
    /// "Issues on board but not in backlog". Reported, never acted on: no command
    /// deletes an item somebody added on purpose.
    /// </summary>
    Extra,

    /// <summary>More than one board item claims the same Epic title or Issue code.</summary>
    Duplicate,

    /// <summary>The board's title differs from the backlog's.</summary>
    TitleDrift,

    /// <summary>The board's description text differs from the backlog's.</summary>
    DescriptionDrift,

    /// <summary>A Done item has a descendant that is not Done.</summary>
    OpenDescendantOfDone,

    /// <summary>A child Task with no matching backlog bullet.</summary>
    StrayTask,

    /// <summary>A backlog bullet with no matching child Task.</summary>
    MissingTask,

    /// <summary>
    /// The board and the backlog hold different numbers of Epics. Kept as its own
    /// finding because the CLI compares Epic counts rather than identities — its
    /// Epic matching is by substring, so a name-by-name diff would report drift the
    /// CLI tolerates, and dropping the count check would lose the one signal it has.
    /// </summary>
    CountMismatch,

    /// <summary>
    /// Every child is Done while the parent is not. Reported for review, never as a
    /// failure: a parent can hold sign-off work of its own, so this is a judgement
    /// call rather than drift.
    /// </summary>
    EveryChildDone,
}

/// <summary>
/// One thing <c>audit</c> found. It names the item in the vocabulary the user has
/// in front of them — the Epic title or the Issue code — and the board id when the
/// item exists there, so a finding can be acted on without a second lookup.
/// </summary>
public sealed record AuditFinding
{
    public required AuditKind Kind { get; init; }

    public required BacklogLevel Level { get; init; }

    public string? Code { get; init; }

    public required string Title { get; init; }

    public int? BoardId { get; init; }

    /// <summary>Every board id involved, for a duplicate set.</summary>
    public IReadOnlyList<int> BoardIds { get; init; } = [];

    /// <summary>What differs, in one line. Never a full description dump.</summary>
    public required string Detail { get; init; }

    public string KindLabel => Kind switch
    {
        AuditKind.Missing => "Missing",
        AuditKind.Extra => "Not in the backlog",
        AuditKind.Duplicate => "Duplicate",
        AuditKind.TitleDrift => "Title drift",
        AuditKind.DescriptionDrift => "Description drift",
        AuditKind.OpenDescendantOfDone => "Open under Done",
        AuditKind.StrayTask => "Stray task",
        AuditKind.CountMismatch => "Count mismatch",
        AuditKind.EveryChildDone => "Every child done",
        _ => "Missing task",
    };

    /// <summary>DESIGN-SYSTEM §5.3: a glyph beside the word, never colour alone.</summary>
    public string Glyph => Kind switch
    {
        AuditKind.Missing or AuditKind.MissingTask => "+",
        AuditKind.Duplicate => "≡",
        // Extra is a question, not a subtraction: nothing deletes an Issue
        // somebody added deliberately, so its glyph must not read as "removed".
        AuditKind.Extra => "?",
        AuditKind.StrayTask => "−",
        AuditKind.OpenDescendantOfDone => "○",
        AuditKind.CountMismatch => "≠",
        AuditKind.EveryChildDone => "●",
        _ => "~",
    };

    public string Badge => Level == BacklogLevel.Epic ? "EPIC" : Code ?? "ISSUE";

    public string BoardReference => BoardId is { } id
        ? $"#{id}"
        : BoardIds.Count > 0
            ? string.Join(", ", BoardIds.Select(i => $"#{i}"))
            : "not on the board";
}

/// <summary>
/// The read-only answer to "has the board drifted from the backlog?" (ABSD-304).
/// It is not a Plan: it authorises nothing and Apply cannot consume it. Acting on
/// a finding means generating the Plan that fixes it, which goes through the same
/// gate as every other write.
/// </summary>
public sealed record AuditReport
{
    public required IReadOnlyList<AuditFinding> Findings { get; init; }

    /// <summary>
    /// Things worth a human's eye that are not drift — today, parents whose
    /// children are all Done. They are held apart from <see cref="Findings"/> on
    /// purpose: the CLI prints them but does not exit 1 on them, and folding them
    /// in would make a clean board read as dirty.
    /// </summary>
    public IReadOnlyList<AuditFinding> Reviews { get; init; } = [];

    /// <summary>The header counts the CLI prints above its result line.</summary>
    public int BoardEpicCount { get; init; }

    public int BacklogEpicCount { get; init; }

    public int BoardIssueCount { get; init; }

    public int BacklogIssueCount { get; init; }

    /// <summary>How many Issues had their Tasks compared against backlog bullets.</summary>
    public int IssuesTaskChecked { get; init; }

    /// <summary>The board snapshot this was computed against, so a hand-off to a
    /// Plan can say whether the board has moved since.</summary>
    public required string BoardFingerprint { get; init; }

    public required string BacklogFingerprint { get; init; }

    public bool IsClean => Findings.Count == 0;

    public int Count(AuditKind kind) => Findings.Count(f => f.Kind == kind);

    /// <summary>Findings that <c>close-children</c> would resolve, for the handoff (ABSD-306).</summary>
    public IReadOnlyList<AuditFinding> OpenDescendantsOfDone =>
        [.. Findings.Where(f => f.Kind == AuditKind.OpenDescendantOfDone)];

    /// <summary>The CLI exits 1 on drift; this is the same statement in the UI's words.</summary>
    public string Summary => IsClean
        ? "The board matches the backlog."
        : $"{Findings.Count} difference{(Findings.Count == 1 ? string.Empty : "s")} between the backlog and the board.";
}
