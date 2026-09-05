using AdoBoardSync.Core.Configuration;
using AdoBoardSync.Core.Results;

namespace AdoBoardSync.Core.Board;

/// <summary>
/// The port every board read and write goes through. Declared in Core, implemented
/// in Infrastructure (ARCHITECTURE.md §3.1), so callers test against a fake board.
/// </summary>
public interface IBoardGateway
{
    /// <summary>Reads every Epic and Issue on the board. Never mutates anything.</summary>
    Task<Result<BoardSnapshot>> ReadAsync(BoardConfig config, CancellationToken cancellationToken = default);

    /// <summary>Creates one work item, optionally parented, and returns its new id.</summary>
    Task<Result<int>> CreateAsync(
        BoardConfig config,
        string workItemType,
        string title,
        string descriptionHtml,
        int? parentId,
        CancellationToken cancellationToken = default);

    /// <summary>Sets fields on an existing work item.</summary>
    Task<Result<bool>> UpdateAsync(
        BoardConfig config,
        int workItemId,
        IReadOnlyList<BoardFieldChange> changes,
        CancellationToken cancellationToken = default);

    /// <summary>Moves one work item to the recycle bin. A repeat after a dropped
    /// connection either re-succeeds or 404s — neither duplicates anything — so a
    /// delete may be retried like a read.</summary>
    Task<Result<bool>> DeleteAsync(
        BoardConfig config,
        int workItemId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates the sprint iteration node under the project root, or brings an
    /// existing node's dates in line, and returns the node. Idempotent by the same
    /// contract as <c>client.py</c>'s <c>ensure_iteration</c>: an existing node is
    /// reported, never duplicated.
    /// </summary>
    Task<Result<IterationNode>> EnsureIterationAsync(
        BoardConfig config,
        string name,
        string? start,
        string? finish,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The team that should own the sprints: the <c>&lt;Project&gt; Team</c> default
    /// when it exists, else the first team, else null when the project has none.
    /// </summary>
    Task<Result<string?>> DefaultTeamAsync(BoardConfig config, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds an existing iteration node to a team's selected sprints so it appears in
    /// that team's Sprints view. Azure DevOps answers 400 when the iteration is
    /// already selected, which the adapter maps to success — the state the caller
    /// asked for is the state that holds.
    /// </summary>
    Task<Result<bool>> AddTeamIterationAsync(
        BoardConfig config,
        string team,
        string identifier,
        CancellationToken cancellationToken = default);
}

/// <summary>One field write. <see cref="Field"/> is an Azure DevOps reference name.</summary>
public sealed record BoardFieldChange(string Field, string Value)
{
    public const string TitleField = "System.Title";
    public const string DescriptionField = "System.Description";
    public const string StateField = "System.State";
    public const string AssignedToField = "System.AssignedTo";
    public const string IterationPathField = "System.IterationPath";
}

/// <summary>
/// A sprint iteration node as the board holds it. <see cref="Identifier"/> is the
/// node GUID, which is what adding the iteration to a team's sprint view needs; it
/// is null only when the board answered without one.
/// </summary>
public sealed record IterationNode(string Name, string? Identifier, string Note)
{
    /// <summary>True when this run created the node rather than finding it.</summary>
    public bool WasCreated => Note.Contains("created", StringComparison.OrdinalIgnoreCase);
}
