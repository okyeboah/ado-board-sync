using AdoBoardSync.Core.Backlog;
using AdoBoardSync.Core.Board;
using AdoBoardSync.Core.Configuration;

namespace AdoBoardSync.Core.Planning;

/// <summary>
///     The iteration-and-ownership half of the lifecycle builders: <c>sprints</c> and
///     <c>assign</c>. Pure like the rest of the builder — snapshot and config in, Plan
///     out, no port touched.
/// </summary>
public static partial class PlanBuilder
{
    public static Plan BuildSprints(
        BoardConfig config,
        BoardSnapshot snapshot,
        string backlogMarkdown,
        bool assignOnly = false,
        bool includeTasks = true,
        bool resetOnMissing = false)
    {
        var storyType = config.Types["story"];
        var taskType = config.Types["task"];

        var notes = new List<string>();
        if (config.Iterations.Count == 0)
        {
            notes.Add(
                "No iterations configured. Add an \"iterations\" array to board.config.json — "
                + "each entry {name, start?, finish?, items:[codes]}.");

            return new Plan
            {
                Command = PlanCommand.Sprints,
                Rows = [],
                Notes = notes,
                ResetOnMissing = resetOnMissing,
                BacklogFingerprint = FingerprintBacklog(backlogMarkdown),
                BoardFingerprint = snapshot.Fingerprint
            };
        }

        // A code listed in more than one iteration belongs to the earliest listed,
        // matching a schedule where an item starts in its first sprint.
        var codeSprint = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var iteration in config.Iterations)
        foreach (var code in iteration.Items)
            codeSprint.TryAdd(code.ToUpperInvariant(), iteration.Name);

        var issuesByCode = IssuesByCode(config, snapshot, storyType);
        var childrenByParent = ChildrenByParent(snapshot);

        var unknown = codeSprint.Keys.Except(issuesByCode.Keys).Order(StringComparer.Ordinal).ToArray();
        var uncovered = issuesByCode.Keys.Except(codeSprint.Keys).Order(StringComparer.Ordinal).ToArray();
        if (unknown.Length > 0)
            notes.Add($"Codes in the config that are not on the board: {string.Join(", ", unknown)}.");

        if (uncovered.Length > 0)
            notes.Add(
                $"Board Issues with no sprint in the config, left where they are: {string.Join(", ", uncovered)}.");

        if (unknown.Length == 0 && uncovered.Length == 0)
            notes.Add("Coverage is complete: every board Issue maps to exactly one sprint.");

        var rows = new List<PlanRow>();

        if (!assignOnly)
            foreach (var iteration in config.Iterations)
            {
                var span = iteration.Start is null && iteration.Finish is null
                    ? "no dates"
                    : $"{iteration.Start ?? "—"} to {iteration.Finish ?? "—"}";

                rows.Add(new PlanRow
                {
                    Operation = PlanOperation.Create,
                    Target = PlanTarget.IterationNode,
                    Level = BacklogLevel.Epic,
                    Title = iteration.Name,
                    Iteration = new IterationSpec(iteration.Name, iteration.Start, iteration.Finish),
                    Changes = [new PlanFieldChange("iteration.dates", string.Empty, span)]
                });
            }

        foreach (var code in codeSprint.Keys.Intersect(issuesByCode.Keys).Order(StringComparer.Ordinal))
        {
            var issue = issuesByCode[code];
            var sprint = codeSprint[code];
            var desiredPath = $@"{config.Project}\{sprint}";

            if (!IterationMatches(issue.IterationPath, desiredPath))
                rows.Add(new PlanRow
                {
                    Operation = PlanOperation.Update,
                    Level = BacklogLevel.Issue,
                    Title = issue.Title.Trim(),
                    Code = code,
                    BoardId = issue.Id,
                    WorkItemType = issue.WorkItemType,
                    Changes =
                    [
                        new PlanFieldChange(
                            BoardFieldChange.IterationPathField, issue.IterationPath, desiredPath)
                    ]
                });

            if (!includeTasks) continue;

            foreach (var task in childrenByParent.GetValueOrDefault(issue.Id, [])
                         .Where(c => c.WorkItemType == taskType)
                         .OrderBy(c => c.Id))
            {
                if (IterationMatches(task.IterationPath, desiredPath)) continue;

                rows.Add(new PlanRow
                {
                    Operation = PlanOperation.Update,
                    Level = BacklogLevel.Issue,
                    Title = task.Title.Trim(),
                    Code = code,
                    BoardId = task.Id,
                    ParentBoardId = issue.Id,
                    WorkItemType = task.WorkItemType,
                    Changes =
                    [
                        new PlanFieldChange(
                            BoardFieldChange.IterationPathField, task.IterationPath, desiredPath)
                    ]
                });
            }
        }

        return new Plan
        {
            Command = PlanCommand.Sprints,
            Rows = rows,
            Notes = notes,
            ResetOnMissing = resetOnMissing,
            BacklogFingerprint = FingerprintBacklog(backlogMarkdown),
            BoardFingerprint = snapshot.Fingerprint
        };
    }

    /// <summary>
    ///     Plans each Issue's — and by default its child Tasks' — assignee from the
    ///     config's <c>assignees</c> map. Azure DevOps has no backlog-driven ownership,
    ///     so this is what makes a planned work split reproducible and reviewable.
    /// </summary>
    public static Plan BuildAssign(
        BoardConfig config,
        BoardSnapshot snapshot,
        string backlogMarkdown,
        bool includeTasks = true,
        bool onlyUnassigned = false)
    {
        var storyType = config.Types["story"];
        var taskType = config.Types["task"];
        var notes = new List<string>();

        // A code listed under more than one identity belongs to the first listed,
        // mirroring sprints, where the earliest bucket claims a shared code.
        var owner = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (identity, codes) in config.Assignees)
        foreach (var code in codes)
            owner.TryAdd(code.ToUpperInvariant(), identity);

        if (owner.Count == 0)
        {
            notes.Add(
                "No assignees configured. Add an \"assignees\" object to board.config.json — "
                + "each key an Azure DevOps identity, each value a list of Issue codes.");

            return new Plan
            {
                Command = PlanCommand.Assign,
                Rows = [],
                Notes = notes,
                BacklogFingerprint = FingerprintBacklog(backlogMarkdown),
                BoardFingerprint = snapshot.Fingerprint
            };
        }

        var issuesByCode = IssuesByCode(config, snapshot, storyType);
        var childrenByParent = ChildrenByParent(snapshot);

        var unknown = owner.Keys.Except(issuesByCode.Keys).Order(StringComparer.Ordinal).ToArray();
        var uncovered = issuesByCode.Keys.Except(owner.Keys).Order(StringComparer.Ordinal).ToArray();
        if (unknown.Length > 0)
            notes.Add($"Codes in the config that are not on the board: {string.Join(", ", unknown)}.");

        if (uncovered.Length > 0)
            notes.Add($"Board Issues with no assignee in the config: {string.Join(", ", uncovered)}.");

        var rows = new List<PlanRow>();

        foreach (var code in owner.Keys.Intersect(issuesByCode.Keys).Order(StringComparer.Ordinal))
        {
            var issue = issuesByCode[code];
            var desired = owner[code];

            // An item that is already correctly owned is shown as Unchanged rather
            // than omitted (PRD-AC-12). The CLI reports the same fact as a trailing
            // count — "; 3 already correct" — and a reviewer needs it either way:
            // a plan that lists two of the five codes they configured is otherwise
            // indistinguishable from one where the other three went missing.
            // Unchanged rows are never written, so this changes what is shown and
            // not what reaches the board.
            rows.Add(AssigneeSettled(issue, desired, onlyUnassigned)
                ? new PlanRow
                {
                    Operation = PlanOperation.Unchanged,
                    Level = BacklogLevel.Issue,
                    Title = issue.Title.Trim(),
                    Code = code,
                    BoardId = issue.Id,
                    WorkItemType = issue.WorkItemType
                }
                : new PlanRow
                {
                    Operation = PlanOperation.Update,
                    Level = BacklogLevel.Issue,
                    Title = issue.Title.Trim(),
                    Code = code,
                    BoardId = issue.Id,
                    WorkItemType = issue.WorkItemType,
                    Changes =
                    [
                        new PlanFieldChange(
                            BoardFieldChange.AssignedToField,
                            issue.IsAssigned ? issue.AssigneeDisplay : "(unassigned)",
                            desired)
                    ]
                });

            if (!includeTasks) continue;

            foreach (var task in childrenByParent.GetValueOrDefault(issue.Id, [])
                         .Where(c => c.WorkItemType == taskType)
                         .OrderBy(c => c.Id))
            {
                if (AssigneeSettled(task, desired, onlyUnassigned)) continue;

                rows.Add(new PlanRow
                {
                    Operation = PlanOperation.Update,
                    Level = BacklogLevel.Issue,
                    Title = task.Title.Trim(),
                    Code = code,
                    BoardId = task.Id,
                    ParentBoardId = issue.Id,
                    WorkItemType = task.WorkItemType,
                    Changes =
                    [
                        new PlanFieldChange(
                            BoardFieldChange.AssignedToField,
                            task.IsAssigned ? task.AssigneeDisplay : "(unassigned)",
                            desired)
                    ]
                });
            }
        }

        return new Plan
        {
            Command = PlanCommand.Assign,
            Rows = rows,
            Notes = notes,
            BacklogFingerprint = FingerprintBacklog(backlogMarkdown),
            BoardFingerprint = snapshot.Fingerprint
        };
    }
}