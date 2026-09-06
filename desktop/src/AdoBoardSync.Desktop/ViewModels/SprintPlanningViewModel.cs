using System.Collections.ObjectModel;
using AdoBoardSync.Core.Configuration;
using AdoBoardSync.Core.Results;
using AdoBoardSync.Desktop.Services;
using AdoBoardSync.Infrastructure;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdoBoardSync.Desktop.ViewModels;

/// <summary>
/// One configured iteration, as the table edits it. Issue codes are held as the
/// text the user typed rather than a parsed list — see
/// <see cref="PlanningRowViewModel" />, which owns that rule for both tables.
/// </summary>
public sealed partial class SprintRowViewModel : PlanningRowViewModel
{
    [ObservableProperty] private string _name = string.Empty;

    [ObservableProperty] private string _start = string.Empty;

    [ObservableProperty] private string _finish = string.Empty;

    public SprintRowViewModel()
    {
    }

    public SprintRowViewModel(IterationConfig iteration)
    {
        ArgumentNullException.ThrowIfNull(iteration);

        Name = iteration.Name;
        Start = iteration.Start ?? string.Empty;
        Finish = iteration.Finish ?? string.Empty;
        Codes = string.Join(", ", iteration.Items);
    }

    public IterationConfig ToConfig() =>
        new(Name.Trim(),
            string.IsNullOrWhiteSpace(Start) ? null : Start.Trim(),
            string.IsNullOrWhiteSpace(Finish) ? null : Finish.Trim(),
            ParsedCodes());
}

/// <summary>
/// The Sprints surface (ABSD-401): the <c>iterations</c> table, edited here and
/// written back to <c>board.config.json</c>. Everything about loading, dirty
/// tracking, saving and coverage lives in <see cref="PlanningTableViewModel{TRow}" />;
/// what is here is what makes this table about sprints rather than assignees.
/// </summary>
public sealed class SprintPlanningViewModel : PlanningTableViewModel<SprintRowViewModel>
{
    private readonly Func<string, IReadOnlyList<IterationConfig>, Result<bool>> _write;

    public SprintPlanningViewModel(
        Func<string, IReadOnlyList<IterationConfig>, Result<bool>>? write = null,
        Func<string, Task<Result<BacklogWorkspace>>>? reload = null)
        : base(reload ?? DefaultReload)
    {
        _write = write ?? BoardConfigWriter.WriteIterations;
    }

    /// <summary>The table, under the name the view and the tests know it by.</summary>
    public ObservableCollection<SprintRowViewModel> Sprints => Rows;

    protected override string PluralNoun => "sprints";

    protected override string EmptyStatus => "No sprints configured yet. Add one to start scheduling.";

    protected override IEnumerable<SprintRowViewModel> RowsFrom(BacklogWorkspace workspace) =>
        workspace.Config.Iterations.Select(iteration => new SprintRowViewModel(iteration));

    protected override SprintRowViewModel NewRow() => new();

    protected override Result<bool> Write(string path) =>
        _write(path, [.. Sprints.Select(sprint => sprint.ToConfig())]);

    protected override string LoadedStatus(int rowCount, int codeCount) =>
        $"{rowCount} sprint(s) · {codeCount} issue(s) scheduled";

    protected override string UnknownCodesNote(IReadOnlyList<string> codes) =>
        $"Scheduled but not in the backlog: {string.Join(", ", codes)}.";

    protected override string UncoveredCodesNote(IReadOnlyList<string> codes) =>
        $"In the backlog with no sprint: {string.Join(", ", codes)}.";

    // The Plan gives a repeated code to the earliest sprint that lists it. Saying
    // so here is cheaper than a user discovering it from a Plan.
    protected override string DuplicatedCodesNote(IReadOnlyList<string> codes) =>
        $"In more than one sprint, and the first listed wins: {string.Join(", ", codes)}.";

    private static Task<Result<BacklogWorkspace>> DefaultReload(string path) =>
        new ProfileLoader(new FileSystemBacklogFileStore()).LoadAsync(path);
}
