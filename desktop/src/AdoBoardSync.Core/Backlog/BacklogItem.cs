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
///
/// <see cref="DescriptionStart"/> and <see cref="DescriptionEnd"/> locate the
/// item's description block in the backlog text — start inclusive, end exclusive,
/// counted in <see cref="Core.PythonCompat.SplitLines"/> lines. They are editor
/// metadata for <see cref="BacklogSplicer"/>, not board semantics: the board
/// model is entirely <see cref="DescriptionLines"/> and <see cref="Bullets"/>,
/// and the parity suite compares only those.
/// </summary>
public sealed record BacklogItem
{
    public required BacklogLevel Level { get; init; }

    public required string Title { get; init; }

    public string? Code { get; init; }

    public IReadOnlyList<string> DescriptionLines { get; init; } = [];

    public IReadOnlyList<string> Bullets { get; init; } = [];

    public int DescriptionStart { get; init; }

    public int DescriptionEnd { get; init; }
}
