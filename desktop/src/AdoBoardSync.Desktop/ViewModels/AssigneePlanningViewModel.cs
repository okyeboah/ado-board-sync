using System.Collections.ObjectModel;
using AdoBoardSync.Core.Configuration;
using AdoBoardSync.Core.Results;
using AdoBoardSync.Desktop.Services;
using AdoBoardSync.Infrastructure;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdoBoardSync.Desktop.ViewModels;

/// <summary>One owner and the Issue codes they hold.</summary>
public sealed partial class AssigneeRowViewModel : PlanningRowViewModel
{
    [ObservableProperty] private string _identity = string.Empty;

    public AssigneeRowViewModel()
    {
    }

    public AssigneeRowViewModel(string identity, IReadOnlyList<string> codes)
    {
        Identity = identity;
        Codes = string.Join(", ", codes);
    }
}

/// <summary>
/// The Assignees surface (ABSD-402): the <c>assignees</c> map, edited here and
/// written back to <c>board.config.json</c>.
///
/// Like the sprint table it edits the profile and never the board, and it shares
/// that table's mechanism (<see cref="PlanningTableViewModel{TRow}" />). Azure
/// DevOps has no backlog-driven ownership — assignment is a per-item field set by
/// hand — so this table is what makes a planned work split reproducible and
/// reviewable before the Assign Plan ever runs.
/// </summary>
public sealed class AssigneePlanningViewModel : PlanningTableViewModel<AssigneeRowViewModel>
{
    private readonly Func<string, IReadOnlyDictionary<string, IReadOnlyList<string>>, Result<bool>> _write;

    public AssigneePlanningViewModel(
        Func<string, IReadOnlyDictionary<string, IReadOnlyList<string>>, Result<bool>>? write = null,
        Func<string, Task<Result<BacklogWorkspace>>>? reload = null)
        : base(reload ?? DefaultReload)
    {
        _write = write ?? BoardConfigWriter.WriteAssignees;
    }

    /// <summary>The table, under the name the view and the tests know it by.</summary>
    public ObservableCollection<AssigneeRowViewModel> Owners => Rows;

    protected override string PluralNoun => "assignees";

    protected override string EmptyStatus => "No assignees configured yet. Add one to plan ownership.";

    protected override IEnumerable<AssigneeRowViewModel> RowsFrom(BacklogWorkspace workspace) =>
        workspace.Config.Assignees
            .OrderBy(assignee => assignee.Key, StringComparer.Ordinal)
            .Select(assignee => new AssigneeRowViewModel(assignee.Key, assignee.Value));

    protected override AssigneeRowViewModel NewRow() => new();

    protected override Result<bool> Write(string path) =>
        _write(path, Owners.ToDictionary(
            owner => owner.Identity.Trim(),
            owner => owner.ParsedCodes(),
            StringComparer.Ordinal));

    /// <summary>
    /// Two rows for one identity would silently collapse into one entry on write,
    /// and the codes in whichever row lost would vanish without a word.
    /// </summary>
    protected override string? RefuseSave()
    {
        var duplicate = Owners
            .GroupBy(owner => owner.Identity.Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);

        return duplicate is null
            ? null
            : $"\"{duplicate.Key}\" appears in two rows. Merge them — one identity holds one "
              + "list of codes. (config.duplicate_assignee)";
    }

    protected override string LoadedStatus(int rowCount, int codeCount) =>
        $"{rowCount} owner(s) · {codeCount} issue(s) owned";

    protected override string UnknownCodesNote(IReadOnlyList<string> codes) =>
        $"Owned but not in the backlog: {string.Join(", ", codes)}.";

    protected override string UncoveredCodesNote(IReadOnlyList<string> codes) =>
        $"In the backlog with no owner: {string.Join(", ", codes)}.";

    // The Plan gives a shared code to the first listed owner, mirroring the sprint
    // table's rule. Saying it here beats discovering it from a Plan.
    protected override string DuplicatedCodesNote(IReadOnlyList<string> codes) =>
        $"Owned by more than one person, and the first listed wins: {string.Join(", ", codes)}.";

    private static Task<Result<BacklogWorkspace>> DefaultReload(string path) =>
        new ProfileLoader(new FileSystemBacklogFileStore()).LoadAsync(path);
}
