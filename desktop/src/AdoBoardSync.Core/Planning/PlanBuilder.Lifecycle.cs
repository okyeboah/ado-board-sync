using AdoBoardSync.Core.Backlog;
using AdoBoardSync.Core.Board;
using AdoBoardSync.Core.Configuration;
using AdoBoardSync.Core.Markdown;
using AdoBoardSync.Core.Results;

namespace AdoBoardSync.Core.Planning;

/// <summary>
/// The commands that change ownership, scheduling and state rather than
/// structure: <c>dedup</c>, <c>sprints</c>, <c>assign</c>, <c>close-children</c>
/// and <c>sync-one</c>. The CLI keeps every one of these out of <c>sync</c> on
/// purpose — <c>sync</c> is a structural reconcile, and these change workflow
/// metadata — so each is planned and confirmed on its own here too.
///
/// Every method is pure: board snapshot in, Plan out. No method reads the network.
/// </summary>
public static partial class PlanBuilder
{
    /// <summary>
    /// Plans the deletion of duplicate work items, keeping the lowest id of each
    /// set. Epics duplicate by title, Issues by code, Tasks by title within one
    /// parent — the three ways this board format can say the same thing twice.
    /// </summary>
    public static Plan BuildDedup(BoardConfig config, BoardSnapshot snapshot, string backlogMarkdown)
    {
        var epicType = config.Types["epic"];
        var storyType = config.Types["story"];
        var taskType = config.Types["task"];

        var byTitle = new Dictionary<(string Type, string Title), List<int>>();
        var byCode = new Dictionary<string, List<(int Id, string Title)>>(StringComparer.Ordinal);
        var tasksByParent = new Dictionary<int, List<(int Id, string Title)>>();

        foreach (var work in snapshot.Items.OrderBy(i => i.Id))
        {
            var title = work.Title.Trim();

            if (work.WorkItemType == epicType)
            {
                AddTo(byTitle, (epicType, title.ToLowerInvariant()), work.Id);
            }
            else if (work.WorkItemType == storyType)
            {
                var match = config.IssueCodeRegex.Match(title);
                if (match.Success)
                {
                    var code = match.Groups[1].Value.ToUpperInvariant();
                    if (!byCode.TryGetValue(code, out var list))
                    {
                        list = [];
                        byCode[code] = list;
                    }

                    list.Add((work.Id, title));
                }
                else
                {
                    // An Issue with no code can only be recognised by its title.
                    AddTo(byTitle, (storyType, title.ToLowerInvariant()), work.Id);
                }
            }
            else if (work.WorkItemType == taskType && work.ParentId is { } parent)
            {
                if (!tasksByParent.TryGetValue(parent, out var list))
                {
                    list = [];
                    tasksByParent[parent] = list;
                }

                list.Add((work.Id, title));
            }
        }

        var byId = snapshot.Items.ToDictionary(i => i.Id);
        var rows = new List<PlanRow>();
        var doomed = new HashSet<int>();

        void Doom(int id, string? code, string detail)
        {
            if (!doomed.Add(id))
            {
                return;
            }

            var work = byId[id];
            rows.Add(new PlanRow
            {
                Operation = PlanOperation.Delete,
                Level = work.WorkItemType == epicType ? BacklogLevel.Epic : BacklogLevel.Issue,
                Title = work.Title.Trim(),
                Code = code,
                BoardId = id,
                WorkItemType = work.WorkItemType,
                Changes = [new PlanFieldChange("duplicate.of", detail, string.Empty)],
            });
        }

        // Ordered by title and code rather than by discovery, so two runs against
        // the same board produce the same Plan in the same order — a Plan a
        // reviewer cannot diff against the last one is harder to trust.
        foreach (var (key, ids) in byTitle.Where(e => e.Value.Count > 1).OrderBy(e => e.Key.Title, StringComparer.Ordinal))
        {
            var keep = ids.Min();
            foreach (var id in ids.Where(i => i != keep).Order())
            {
                Doom(id, null, $"duplicate of #{keep} ({key.Type} '{key.Title}')");
            }
        }

        foreach (var (code, entries) in byCode.Where(e => e.Value.Count > 1).OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            var keep = entries.Min(e => e.Id);
            foreach (var (id, _) in entries.Where(e => e.Id != keep).OrderBy(e => e.Id))
            {
                Doom(id, code, $"duplicate of #{keep} ({code})");
            }
        }

        foreach (var (parent, tasks) in tasksByParent.OrderBy(e => e.Key))
        {
            foreach (var group in tasks
                         .GroupBy(t => t.Title.ToLowerInvariant(), StringComparer.Ordinal)
                         .Where(g => g.Count() > 1)
                         .OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var keep = group.Min(t => t.Id);
                foreach (var (id, _) in group.Where(t => t.Id != keep).OrderBy(t => t.Id))
                {
                    Doom(id, null, $"duplicate Task of #{keep} under #{parent}");
                }
            }
        }

        // A duplicate Epic or Issue takes its child Tasks with it. A plain work-item
        // DELETE does not remove children, so without this the Tasks under a deleted
        // duplicate are left orphaned — and a duplicate parent's Tasks are themselves
        // duplicates of the kept parent's.
        foreach (var parent in doomed.Order().ToArray())
        {
            foreach (var (id, _) in tasksByParent.GetValueOrDefault(parent, []).OrderBy(t => t.Id))
            {
                Doom(id, null, $"child Task of removed #{parent} (cascade)");
            }
        }

        return new Plan
        {
            Command = PlanCommand.Dedup,
            Rows = rows,
            BacklogFingerprint = FingerprintBacklog(backlogMarkdown),
            BoardFingerprint = snapshot.Fingerprint,
        };
    }

    /// <summary>
    /// Plans the configured sprint iterations and the items that move into them.
    ///
    /// An iteration row is a <see cref="PlanTarget.IterationNode"/> Create. It reads
    /// as "create" even for a node that already exists, because nothing short of a
    /// second network read can tell the difference at plan time and the Plan must
    /// stay pure. Apply calls the idempotent ensure, which reports "exists" rather
    /// than creating a second node — so the row over-states at worst, never writes
    /// twice.
    /// </summary>
    public static Plan BuildSprints(
        BoardConfig config,
        BoardSnapshot snapshot,
        string backlogMarkdown,
        bool assignOnly = false,
        bool includeTasks = true)
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
                BacklogFingerprint = FingerprintBacklog(backlogMarkdown),
                BoardFingerprint = snapshot.Fingerprint,
            };
        }

        // A code listed in more than one iteration belongs to the earliest listed,
        // matching a schedule where an item starts in its first sprint.
        var codeSprint = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var iteration in config.Iterations)
        {
            foreach (var code in iteration.Items)
            {
                codeSprint.TryAdd(code.ToUpperInvariant(), iteration.Name);
            }
        }

        var issuesByCode = IssuesByCode(config, snapshot, storyType);
        var childrenByParent = ChildrenByParent(snapshot);

        var unknown = codeSprint.Keys.Except(issuesByCode.Keys).Order(StringComparer.Ordinal).ToArray();
        var uncovered = issuesByCode.Keys.Except(codeSprint.Keys).Order(StringComparer.Ordinal).ToArray();
        if (unknown.Length > 0)
        {
            notes.Add($"Codes in the config that are not on the board: {string.Join(", ", unknown)}.");
        }

        if (uncovered.Length > 0)
        {
            notes.Add($"Board Issues with no sprint in the config, left where they are: {string.Join(", ", uncovered)}.");
        }

        if (unknown.Length == 0 && uncovered.Length == 0)
        {
            notes.Add("Coverage is complete: every board Issue maps to exactly one sprint.");
        }

        var rows = new List<PlanRow>();

        if (!assignOnly)
        {
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
                    Changes = [new PlanFieldChange("iteration.dates", string.Empty, span)],
                });
            }
        }

        foreach (var code in codeSprint.Keys.Intersect(issuesByCode.Keys).Order(StringComparer.Ordinal))
        {
            var issue = issuesByCode[code];
            var sprint = codeSprint[code];
            var desiredPath = $@"{config.Project}\{sprint}";

            if (!IterationMatches(issue.IterationPath, desiredPath))
            {
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
                            BoardFieldChange.IterationPathField, issue.IterationPath, desiredPath),
                    ],
                });
            }

            if (!includeTasks)
            {
                continue;
            }

            foreach (var task in childrenByParent.GetValueOrDefault(issue.Id, [])
                         .Where(c => c.WorkItemType == taskType)
                         .OrderBy(c => c.Id))
            {
                if (IterationMatches(task.IterationPath, desiredPath))
                {
                    continue;
                }

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
                            BoardFieldChange.IterationPathField, task.IterationPath, desiredPath),
                    ],
                });
            }
        }

        return new Plan
        {
            Command = PlanCommand.Sprints,
            Rows = rows,
            Notes = notes,
            BacklogFingerprint = FingerprintBacklog(backlogMarkdown),
            BoardFingerprint = snapshot.Fingerprint,
        };
    }

    /// <summary>
    /// Plans each Issue's — and by default its child Tasks' — assignee from the
    /// config's <c>assignees</c> map. Azure DevOps has no backlog-driven ownership,
    /// so this is what makes a planned work split reproducible and reviewable.
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
        {
            foreach (var code in codes)
            {
                owner.TryAdd(code.ToUpperInvariant(), identity);
            }
        }

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
                BoardFingerprint = snapshot.Fingerprint,
            };
        }

        var issuesByCode = IssuesByCode(config, snapshot, storyType);
        var childrenByParent = ChildrenByParent(snapshot);

        var unknown = owner.Keys.Except(issuesByCode.Keys).Order(StringComparer.Ordinal).ToArray();
        var uncovered = issuesByCode.Keys.Except(owner.Keys).Order(StringComparer.Ordinal).ToArray();
        if (unknown.Length > 0)
        {
            notes.Add($"Codes in the config that are not on the board: {string.Join(", ", unknown)}.");
        }

        if (uncovered.Length > 0)
        {
            notes.Add($"Board Issues with no assignee in the config: {string.Join(", ", uncovered)}.");
        }

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
                    WorkItemType = issue.WorkItemType,
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
                            desired),
                    ],
                });

            if (!includeTasks)
            {
                continue;
            }

            foreach (var task in childrenByParent.GetValueOrDefault(issue.Id, [])
                         .Where(c => c.WorkItemType == taskType)
                         .OrderBy(c => c.Id))
            {
                if (AssigneeSettled(task, desired, onlyUnassigned))
                {
                    continue;
                }

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
                            desired),
                    ],
                });
            }
        }

        return new Plan
        {
            Command = PlanCommand.Assign,
            Rows = rows,
            Notes = notes,
            BacklogFingerprint = FingerprintBacklog(backlogMarkdown),
            BoardFingerprint = snapshot.Fingerprint,
        };
    }

    /// <summary>
    /// Plans the terminal state for every open descendant of an item already Done,
    /// at any depth. Azure DevOps propagates state upward but never downward, so
    /// marking an Epic Done leaves its Issues and Tasks open indefinitely.
    ///
    /// With <paramref name="assignFromParent"/> the Done ancestor's assignee is
    /// copied onto each closed item that is <em>currently unassigned</em> — never
    /// over an assignee somebody set deliberately.
    /// </summary>
    public static Plan BuildCloseChildren(
        BoardConfig config,
        BoardSnapshot snapshot,
        string backlogMarkdown,
        bool assignFromParent = false)
    {
        var done = config.States["done"];
        var childrenByParent = ChildrenByParent(snapshot);
        var (downward, _) = StateDrift(snapshot, childrenByParent, done);
        var byId = snapshot.Items.ToDictionary(i => i.Id);

        var rows = new List<PlanRow>();

        // Grouped by the Done ancestor that explains the closure, so a reviewer
        // reads "this Epic is done, so these five close" rather than a flat list.
        foreach (var ancestor in downward.Values.Distinct().Order())
        {
            var parentAssignee = assignFromParent ? byId[ancestor].AssignedTo : string.Empty;

            foreach (var id in downward.Where(d => d.Value == ancestor).Select(d => d.Key).Order())
            {
                var work = byId[id];
                var changes = new List<PlanFieldChange>
                {
                    new(BoardFieldChange.StateField, work.State, done),
                };

                if (parentAssignee.Length > 0 && work.AssignedTo.Length == 0)
                {
                    changes.Add(new PlanFieldChange(
                        BoardFieldChange.AssignedToField, "(unassigned)", parentAssignee));
                }

                rows.Add(new PlanRow
                {
                    Operation = PlanOperation.Update,
                    Level = work.WorkItemType == config.Types["epic"] ? BacklogLevel.Epic : BacklogLevel.Issue,
                    Title = work.Title.Trim(),
                    Code = config.IssueCodeRegex.Match(work.Title) is { Success: true } match
                        ? match.Groups[1].Value.ToUpperInvariant()
                        : null,
                    BoardId = id,
                    ParentBoardId = ancestor,
                    WorkItemType = work.WorkItemType,
                    Changes = changes,
                });
            }
        }

        return new Plan
        {
            Command = PlanCommand.CloseChildren,
            Rows = rows,
            BacklogFingerprint = FingerprintBacklog(backlogMarkdown),
            BoardFingerprint = snapshot.Fingerprint,
        };
    }

    /// <summary>
    /// Plans exactly one Issue: create or update it, and put it in one configured
    /// sprint. It never changes Tasks or assignees — the narrow tool for when one
    /// item has moved and a full reconcile is not wanted.
    ///
    /// Returns a failure rather than an empty Plan when the request cannot be
    /// honoured, because "nothing to do" and "that code is not in your backlog"
    /// must not look the same to the person who typed the code.
    /// </summary>
    public static Result<Plan> BuildSyncOne(
        BoardConfig config,
        IReadOnlyList<BacklogItem> items,
        BoardSnapshot snapshot,
        string backlogMarkdown,
        string issueCode,
        string sprintName)
    {
        var code = issueCode.Trim().ToUpperInvariant();
        if (!config.IssueCodeRegex.IsMatch(code) || config.IssueCodeRegex.Match(code).Value != code)
        {
            return Error.Validation(
                "syncone.bad_code",
                $"{issueCode} is not an Issue code. It must read {config.CodePrefix}-<digits>.");
        }

        if (!config.Iterations.Any(i => string.Equals(i.Name, sprintName, StringComparison.Ordinal)))
        {
            return Error.Validation(
                "syncone.unknown_sprint",
                $"Sprint \"{sprintName}\" is not one of the iterations in board.config.json.");
        }

        BacklogItem? issue = null;
        BacklogItem? epic = null;
        foreach (var item in items)
        {
            if (item.Level == BacklogLevel.Epic)
            {
                epic = item;
            }
            else if (string.Equals(item.Code?.ToUpperInvariant(), code, StringComparison.Ordinal))
            {
                issue = item;
                break;
            }
        }

        if (issue is null || epic is null)
        {
            return Error.NotFound(
                "syncone.not_in_backlog",
                $"{code} is not in the backlog under an Epic heading, so there is nothing to sync.");
        }

        var storyType = config.Types["story"];

        // Scoped to the Issue type on purpose. A Task may cite another ticket's
        // code in its own title, and an unscoped match made the cited ticket
        // unsyncable — the same defect the CLI carries a comment about.
        var matches = snapshot.Items
            .Where(i => i.WorkItemType == storyType &&
                        i.Title.Contains(code, StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => i.Id)
            .ToArray();

        if (matches.Length > 1)
        {
            return Error.Conflict(
                "syncone.ambiguous",
                $"{code} matches {matches.Length} work items on the board ({string.Join(", ", matches.Select(m => $"#{m.Id}"))}). Run dedup first.");
        }

        var title = issue.Title.Trim();
        var html = MarkdownHtml.ToHtml(issue.DescriptionLines);
        var desiredPath = $@"{config.Project}\{sprintName}";
        var rows = new List<PlanRow>();

        if (matches.Length == 1)
        {
            var work = matches[0];
            var changes = new List<PlanFieldChange>();

            if (!string.Equals(work.Title.Trim(), title, StringComparison.Ordinal))
            {
                changes.Add(new PlanFieldChange(BoardFieldChange.TitleField, work.Title.Trim(), title));
            }

            if (MarkdownHtml.Normalize(work.Description) != MarkdownHtml.Normalize(html))
            {
                changes.Add(new PlanFieldChange(BoardFieldChange.DescriptionField, work.Description, html));
            }

            if (!IterationMatches(work.IterationPath, desiredPath))
            {
                changes.Add(new PlanFieldChange(
                    BoardFieldChange.IterationPathField, work.IterationPath, desiredPath));
            }

            rows.Add(changes.Count == 0
                ? new PlanRow
                {
                    Operation = PlanOperation.Unchanged,
                    Level = BacklogLevel.Issue,
                    Title = title,
                    Code = code,
                    BoardId = work.Id,
                    DescriptionHtml = html,
                }
                : new PlanRow
                {
                    Operation = PlanOperation.Update,
                    Level = BacklogLevel.Issue,
                    Title = title,
                    Code = code,
                    BoardId = work.Id,
                    WorkItemType = storyType,
                    DescriptionHtml = html,
                    Changes = changes,
                });
        }
        else
        {
            var epicTitle = epic.Title.Trim().ToLowerInvariant();
            var epicType = config.Types["epic"];
            var parents = snapshot.Items
                .Where(i => i.WorkItemType == epicType &&
                            string.Equals(i.Title.Trim().ToLowerInvariant(), epicTitle, StringComparison.Ordinal))
                .OrderBy(i => i.Id)
                .ToArray();

            if (parents.Length != 1)
            {
                return Error.NotFound(
                    "syncone.parent_ambiguous",
                    $"Expected exactly one board Epic titled \"{epic.Title.Trim()}\" to parent {code}; found {parents.Length}.");
            }

            rows.Add(new PlanRow
            {
                Operation = PlanOperation.Create,
                Level = BacklogLevel.Issue,
                Title = title,
                Code = code,
                ParentBoardId = parents[0].Id,
                WorkItemType = storyType,
                DescriptionHtml = html,
                Changes =
                [
                    new PlanFieldChange(BoardFieldChange.IterationPathField, string.Empty, desiredPath),
                ],
            });
        }

        return new Plan
        {
            Command = PlanCommand.SyncOne,
            Rows = rows,
            BacklogFingerprint = FingerprintBacklog(backlogMarkdown),
            BoardFingerprint = snapshot.Fingerprint,
        };
    }

    /// <summary>Board Issues that carry a code, keyed by it. Lowest id wins a repeat.</summary>
    private static Dictionary<string, BoardWorkItem> IssuesByCode(
        BoardConfig config, BoardSnapshot snapshot, string storyType)
    {
        var issues = new Dictionary<string, BoardWorkItem>(StringComparer.Ordinal);
        foreach (var work in snapshot.Items.Where(i => i.WorkItemType == storyType).OrderBy(i => i.Id))
        {
            var match = config.IssueCodeRegex.Match(work.Title);
            if (match.Success)
            {
                issues.TryAdd(match.Groups[1].Value.ToUpperInvariant(), work);
            }
        }

        return issues;
    }

    /// <summary>
    /// Whether an item is already where the plan wants it. Unlike AssignedTo,
    /// IterationPath comes back as a plain string, so this is a direct comparison —
    /// trimmed and case-insensitive, because Azure DevOps echoes back the path with
    /// the project's own casing rather than the caller's.
    /// </summary>
    private static bool IterationMatches(string current, string desired) =>
        current.Length != 0 &&
        string.Equals(current.Trim(), desired.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when no assignee write is needed: the item already resolves to the
    /// wanted identity, or the caller asked to fill only unassigned items and this
    /// one is already owned by somebody.
    ///
    /// "Already resolves" is the CLI's own three-facet rule — <c>uniqueName</c>,
    /// <c>id</c> or <c>displayName</c>. Comparing one facet only would plan a write
    /// for an item that is already correctly owned, and since that write does not
    /// change what the next read returns, the same row would come back on every run.
    /// </summary>
    private static bool AssigneeSettled(BoardWorkItem work, string desired, bool onlyUnassigned) =>
        work.IsAssigned && (onlyUnassigned || work.AssigneeIs(desired));

    private static void AddTo(Dictionary<(string, string), List<int>> map, (string, string) key, int id)
    {
        if (!map.TryGetValue(key, out var ids))
        {
            ids = [];
            map[key] = ids;
        }

        ids.Add(id);
    }
}
