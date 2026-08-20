namespace AdoBoardSync.Core.Backlog;

public enum BacklogLevel
{
    Epic,
    Issue
}

/// <summary>
/// One parsed backlog item in document order.
///
/// <see cref="Code"/> is set for an Issue and null for an Epic; the code is the
/// stable identity used to match a backlog Issue to a board work item across
/// renames. <see cref="Bullets"/> holds an Issue's Task bullets and is empty for
/// an Epic.
/// </summary>
public sealed record BacklogItem
{
    public required BacklogLevel Level { get; init; }

    public required string Title { get; init; }

    public string? Code { get; init; }

    public IReadOnlyList<string> DescriptionLines { get; init; } = [];

    public IReadOnlyList<string> Bullets { get; init; } = [];
}
