using AdoBoardSync.Core.Backlog;
using AdoBoardSync.Core.Board;
using AdoBoardSync.Core.Configuration;
using AdoBoardSync.Core.Results;

namespace AdoBoardSync.Core.Planning;

/// <summary>
///     The two id-and-evidence commands the CLI grew after the FSD's first command
///     table: <c>set-state</c> and <c>advance</c>. Both plan against the board
///     snapshot alone — neither reads the backlog's descriptions — and both write
///     only the state field, so Apply needs nothing beyond the ordinary update path.
/// </summary>
public static partial class PlanBuilder
{
    /// <summary>
    ///     The CLI's <c>set-state</c>: move work items named by board id to a target
    ///     state (the configured terminal state by default), ticking a leading
    ///     <c>[ ]</c> title checkbox on the way to Done unless told not to. An id the
    ///     board does not hold becomes a note, matching the CLI's WARN.
    /// </summary>
    public static Plan BuildSetState(
        BoardConfig config,
        BoardSnapshot snapshot,
        string backlogMarkdown,
        IReadOnlyList<int> ids,
        string? targetState = null,
        bool noTick = false)
    {
        var epicType = config.Types["epic"];
        var target = string.IsNullOrWhiteSpace(targetState)
            ? config.States["done"]
            : targetState.Trim();
        var tick = !noTick && target == config.States["done"];

        var notes = new List<string>();
        var byId = snapshot.Items.ToLookup(i => i.Id);

        var missing = ids.Where(id => !byId.Contains(id)).Order().ToArray();
        if (missing.Length > 0)
            notes.Add($"Not found on this board: {string.Join(", ", missing.Select(i => $"#{i}"))}.");

        var rows = new List<PlanRow>();
        foreach (var id in ids.Distinct().Order())
        {
            var work = byId[id].FirstOrDefault();
            if (work is null) continue;

            var rawTitle = work.Title;
            var needsState = work.State != target;
            var needsTick = tick && rawTitle.StartsWith("[ ]", StringComparison.Ordinal);

            if (!needsState && !needsTick)
            {
                rows.Add(new PlanRow
                {
                    Operation = PlanOperation.Unchanged,
                    Level = work.WorkItemType == epicType ? BacklogLevel.Epic : BacklogLevel.Issue,
                    Title = rawTitle.Trim(),
                    BoardId = work.Id,
                    WorkItemType = work.WorkItemType
                });
                continue;
            }

            var changes = new List<PlanFieldChange>();
            if (needsState) changes.Add(new PlanFieldChange(BoardFieldChange.StateField, work.State, target));

            if (needsTick)
                changes.Add(new PlanFieldChange(
                    BoardFieldChange.TitleField, rawTitle, "[x]" + rawTitle["[ ]".Length..]));

            rows.Add(new PlanRow
            {
                Operation = PlanOperation.Update,
                Level = work.WorkItemType == epicType ? BacklogLevel.Epic : BacklogLevel.Issue,
                Title = rawTitle.Trim(),
                BoardId = work.Id,
                WorkItemType = work.WorkItemType,
                Changes = changes
            });
        }

        return new Plan
        {
            Command = PlanCommand.SetState,
            Rows = rows,
            Notes = notes,
            BacklogFingerprint = FingerprintBacklog(backlogMarkdown),
            BoardFingerprint = snapshot.Fingerprint
        };
    }

    /// <summary>
    ///     The CLI's <c>advance</c>: move each Issue whose feature branches hold
    ///     commits beyond the base ref from the configured start state to the working
    ///     state. Only that one transition is ever planned — Done is never set from
    ///     git evidence, because merge detection fails in both directions.
    /// </summary>
    public static Result<Plan> BuildAdvance(
        BoardConfig config,
        IReadOnlyList<BacklogItem> items,
        BoardSnapshot snapshot,
        string backlogMarkdown,
        GitProbeReport probe,
        string baseRef)
    {
        var todo = config.States.GetValueOrDefault("todo");
        var doing = config.States.GetValueOrDefault("doing");
        if (string.IsNullOrEmpty(todo) || string.IsNullOrEmpty(doing))
            return Error.Validation(
                "states.not_configured",
                "advance needs the start and working state names of your process template. "
                + "Set 'states.todo' and 'states.doing' in board.config.json — for example "
                + "{\"todo\": \"New\", \"doing\": \"Active\"} (Agile/CMMI) or "
                + "{\"todo\": \"To Do\", \"doing\": \"Doing\"} (Scrum).");

        var storyType = config.Types["story"];
        var issuesByCode = IssuesByCode(config, snapshot, storyType);

        var backlogCodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
            if (item.Level == BacklogLevel.Issue && item.Code is { Length: > 0 } code)
                backlogCodes.Add(code.ToUpperInvariant());

        var notes = new List<string>();
        foreach (var skipped in probe.SkippedRepos) notes.Add(skipped);

        var evidenceByCode = new Dictionary<string, List<GitBranchEvidence>>(StringComparer.Ordinal);
        foreach (var evidence in probe.Evidence)
        {
            var code = evidence.Code.ToUpperInvariant();
            if (!backlogCodes.Contains(code)) continue;

            if (!evidenceByCode.TryGetValue(code, out var branches))
            {
                branches = [];
                evidenceByCode[code] = branches;
            }

            branches.Add(evidence);
        }

        var rows = new List<PlanRow>();
        foreach (var code in evidenceByCode.Keys.Order(StringComparer.Ordinal))
        {
            if (!issuesByCode.TryGetValue(code, out var issue))
            {
                notes.Add($"{code} has commit evidence but is not an Issue on this board.");
                continue;
            }

            // The CLI shows at most three proving branches per code; a fourth adds
            // nothing to a review that can open the repository.
            var detail = string.Join("; ", evidenceByCode[code].Take(3).Select(e => e.Describe()));

            if (issue.State == todo)
                rows.Add(new PlanRow
                {
                    Operation = PlanOperation.Update,
                    Level = BacklogLevel.Issue,
                    Title = issue.Title.Trim(),
                    Code = code,
                    BoardId = issue.Id,
                    WorkItemType = issue.WorkItemType,
                    Detail = detail,
                    Changes =
                    [
                        new PlanFieldChange(BoardFieldChange.StateField, issue.State, doing)
                    ]
                });
            else
                rows.Add(new PlanRow
                {
                    Operation = PlanOperation.Unchanged,
                    Level = BacklogLevel.Issue,
                    Title = issue.Title.Trim(),
                    Code = code,
                    BoardId = issue.Id,
                    WorkItemType = issue.WorkItemType,
                    Detail = detail
                });
        }

        return new Plan
        {
            Command = PlanCommand.Advance,
            Rows = rows,
            Notes = notes,
            BacklogFingerprint = FingerprintBacklog(backlogMarkdown),
            BoardFingerprint = snapshot.Fingerprint
        };
    }
}