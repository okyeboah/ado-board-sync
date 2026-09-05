using System.Security.Cryptography;
using System.Text;

namespace AdoBoardSync.Core.Board;

/// <summary>
/// One work item as the connector maps it at the boundary. The Plan Builder and
/// everything above it see this type, never Azure DevOps' field-bag JSON.
/// <see cref="ParentId"/> rides along on the same batched read as every other
/// field, so a hierarchy question never costs a per-item relations round trip.
///
/// <see cref="State"/>, <see cref="AssignedTo"/> and <see cref="IterationPath"/>
/// ride along for the same reason: <c>close-children</c> needs the state of every
/// descendant, <c>assign</c> needs to know who already owns an item, and
/// <c>sprints</c> needs the iteration each item currently sits in. Fetching them
/// per item at plan time would turn one batched read into hundreds of round trips.
/// </summary>
public sealed record BoardWorkItem
{
    public required int Id { get; init; }

    public required string Title { get; init; }

    public required string WorkItemType { get; init; }

    public string Description { get; init; } = string.Empty;

    /// <summary>The id of this item's parent, when it has one on this board.</summary>
    public int? ParentId { get; init; }

    /// <summary>The workflow state name, for example "New", "Active" or "Done".</summary>
    public string State { get; init; } = string.Empty;

    /// <summary>
    /// The assignee's unique name (the sign-in address) when the board has one, else
    /// its identity id. This is the value a PATCH takes, so it is what an assign
    /// write sends.
    /// </summary>
    public string AssignedTo { get; init; } = string.Empty;

    /// <summary>
    /// The identity id, kept beside <see cref="AssignedTo"/> rather than folded into
    /// it. The CLI's <c>_assignee_matches</c> compares the configured identity
    /// against <c>uniqueName</c>, <c>id</c> AND <c>displayName</c>, so a config that
    /// names people any of those three ways is already assigned as far as the CLI is
    /// concerned. Flattening to one facet would make the desktop app plan a write the
    /// CLI does not — and, because the write does not change what the next read
    /// returns, plan the very same write again on every run.
    /// </summary>
    public string AssignedToId { get; init; } = string.Empty;

    /// <summary>The assignee's display name, the third facet the CLI matches on.</summary>
    public string AssignedToDisplayName { get; init; } = string.Empty;

    /// <summary>True when the board has an assignee at all, by any facet.</summary>
    public bool IsAssigned =>
        AssignedTo.Length > 0 || AssignedToId.Length > 0 || AssignedToDisplayName.Length > 0;

    /// <summary>
    /// Whether this item is already owned by <paramref name="wanted"/>, by the CLI's
    /// own rule: any of the three identity facets, trimmed and case-insensitive.
    /// </summary>
    public bool AssigneeIs(string wanted)
    {
        var want = wanted.Trim();
        return Matches(AssignedTo) || Matches(AssignedToId) || Matches(AssignedToDisplayName);

        bool Matches(string facet) =>
            facet.Length > 0 && string.Equals(facet.Trim(), want, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>What to show for the current owner: the human name when there is one.</summary>
    public string AssigneeDisplay =>
        AssignedToDisplayName.Length > 0 ? AssignedToDisplayName
        : AssignedTo.Length > 0 ? AssignedTo
        : AssignedToId;

    /// <summary>The full iteration path, for example "Project\\Sprint 1".</summary>
    public string IterationPath { get; init; } = string.Empty;
}

/// <summary>
/// Everything one board read returned, plus a fingerprint of it. A Plan records
/// the fingerprint it was computed against, and Apply refuses to run if the board
/// has moved since.
/// </summary>
public sealed record BoardSnapshot(IReadOnlyList<BoardWorkItem> Items, string Fingerprint)
{
    // Unit and record separators: control characters that cannot appear in a
    // work-item title or description, so two different item lists cannot collide
    // by concatenating into the same string.
    private const char FieldSeparator = '\u001F';
    private const char RecordSeparator = '\u001E';

    public static BoardSnapshot From(IEnumerable<BoardWorkItem> items)
    {
        var ordered = items.OrderBy(i => i.Id).ToArray();
        return new BoardSnapshot(ordered, Compute(ordered));
    }

    private static string Compute(IReadOnlyList<BoardWorkItem> items)
    {
        var builder = new StringBuilder();
        foreach (var item in items)
        {
            // Every field a Plan can be computed from is in the hash, because the
            // hash is the stale-plan guard: a field a Plan reads but the guard
            // ignores is a field that can change under an approved Plan.
            // Title and description are here because resync exists to change them;
            // parent because resync-tasks plans against Task parentage; state,
            // assignee and iteration because close-children, assign and sprints
            // each plan against exactly those.
            builder.Append(item.Id).Append(FieldSeparator)
                .Append(item.WorkItemType).Append(FieldSeparator)
                .Append(item.Title).Append(FieldSeparator)
                .Append(item.Description).Append(FieldSeparator)
                .Append(item.ParentId?.ToString() ?? string.Empty).Append(FieldSeparator)
                .Append(item.State).Append(FieldSeparator)
                .Append(item.AssignedTo).Append(FieldSeparator)
                // Every identity facet is in the hash because assign plans against
                // every one of them: a display-name change alone can turn a settled
                // item into one the next Plan wants to write.
                .Append(item.AssignedToId).Append(FieldSeparator)
                .Append(item.AssignedToDisplayName).Append(FieldSeparator)
                .Append(item.IterationPath).Append(RecordSeparator);
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}
