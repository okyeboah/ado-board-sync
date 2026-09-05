using AdoBoardSync.Core.Backlog;
using AdoBoardSync.Core.Board;
using AdoBoardSync.Core.Configuration;
using AdoBoardSync.Core.Markdown;

namespace AdoBoardSync.Core.Planning;

/// <summary>
/// The read-only half of the builder: <c>audit</c>. It answers "has the board
/// drifted from the backlog?" and authorises nothing — acting on a finding means
/// generating the Plan that fixes it, which goes through the same gate as every
/// other write.
///
/// Ported from the CLI's <c>commands.audit</c>, check for check. Where the CLI
/// prints a line this returns a finding, and the two must agree on whether a given
/// board passes: a board the CLI exits 1 on is a board this reports as not clean.
/// </summary>
public static partial class PlanBuilder
{
    public static AuditReport BuildAudit(
        BoardConfig config,
        IReadOnlyList<BacklogItem> items,
        BoardSnapshot snapshot,
        string backlogMarkdown)
    {
        var epicType = config.Types["epic"];
        var storyType = config.Types["story"];
        var taskType = config.Types["task"];
        var done = config.States["done"];

        // The backlog side, built the way csvio.backlog_maps builds it: descriptions
        // rendered with the same converter gen-csv uses, so the comparison below is
        // apples to apples rather than Markdown against HTML.
        var wantEpics = new Dictionary<string, string>(StringComparer.Ordinal);
        var wantIssues = new Dictionary<string, (string Title, string Html)>(StringComparer.Ordinal);
        var bulletsByCode = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        foreach (var item in items)
        {
            var html = MarkdownHtml.ToHtml(item.DescriptionLines);
            if (item.Level == BacklogLevel.Epic)
            {
                wantEpics[item.Title.Trim().ToLowerInvariant()] = html;
            }
            else if (item.Code is { Length: > 0 } code)
            {
                var key = code.ToUpperInvariant();
                wantIssues[key] = (item.Title.Trim(), html);
                bulletsByCode[key] = item.Bullets;
            }
        }

        // The board side. One snapshot serves every check below — Epic/Issue
        // identity, title and description parity, Task parity per Issue, and
        // state-versus-hierarchy agreement. The CLI collapses these into one WIQL
        // plus one batched read for the same reason.
        var boardEpics = new Dictionary<int, BoardWorkItem>();
        var epicTitleIds = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var boardIssues = new Dictionary<string, BoardWorkItem>(StringComparer.Ordinal);
        var issueCodeIds = new Dictionary<string, List<int>>(StringComparer.Ordinal);

        foreach (var work in snapshot.Items.OrderBy(i => i.Id))
        {
            var title = work.Title.Trim();
            if (work.WorkItemType == epicType)
            {
                boardEpics[work.Id] = work;
                Add(epicTitleIds, title.ToLowerInvariant(), work.Id);
            }
            else if (work.WorkItemType == storyType)
            {
                var match = config.IssueCodeRegex.Match(title);
                if (match.Success)
                {
                    var code = match.Groups[1].Value.ToUpperInvariant();
                    boardIssues.TryAdd(code, work);
                    Add(issueCodeIds, code, work.Id);
                }
            }

            // A Task whose title cites another ticket's code — "…surfaced to
            // monitoring (PROJ-101)" on a PROJ-105 task — is neither a duplicate
            // Issue nor its description's twin. Sorting strictly on Epic and Story
            // types is what keeps a cited code from inventing both findings; a bare
            // else branch here was a real defect on boards where tasks cite codes.
        }

        var findings = new List<AuditFinding>();

        // Duplicates collapse into a single map entry above, so they are invisible
        // to every other check. This is the gate that keeps the board unique.
        foreach (var (title, ids) in epicTitleIds.Where(e => e.Value.Count > 1).OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            findings.Add(new AuditFinding
            {
                Kind = AuditKind.Duplicate,
                Level = BacklogLevel.Epic,
                Title = boardEpics[ids[0]].Title.Trim(),
                BoardIds = [.. ids.Order()],
                Detail = $"{ids.Count} work items share this Epic title. Run dedup to keep #{ids.Min()}.",
            });
        }

        foreach (var (code, ids) in issueCodeIds.Where(e => e.Value.Count > 1).OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            findings.Add(new AuditFinding
            {
                Kind = AuditKind.Duplicate,
                Level = BacklogLevel.Issue,
                Code = code,
                Title = boardIssues[code].Title.Trim(),
                BoardIds = [.. ids.Order()],
                Detail = $"{ids.Count} work items carry {code}. Run dedup to keep #{ids.Min()}.",
            });
        }

        // The CLI compares Epic counts rather than identities, because its Epic
        // matching is by substring and a name-by-name diff would report drift the
        // CLI tolerates. The count check is kept for exactly that parity, and the
        // per-Epic findings below add the detail a count cannot give.
        if (boardEpics.Count != wantEpics.Count)
        {
            findings.Add(new AuditFinding
            {
                Kind = AuditKind.CountMismatch,
                Level = BacklogLevel.Epic,
                Title = "Epic count",
                Detail = $"The board has {boardEpics.Count} Epic(s); the backlog has {wantEpics.Count}.",
            });
        }

        var matchedEpicIds = new HashSet<int>();
        foreach (var wantedTitle in wantEpics.Keys.Order(StringComparer.Ordinal))
        {
            var found = MatchEpic(boardEpics, wantedTitle);
            if (found is { } id)
            {
                matchedEpicIds.Add(id);
                continue;
            }

            findings.Add(new AuditFinding
            {
                Kind = AuditKind.Missing,
                Level = BacklogLevel.Epic,
                Title = wantedTitle,
                Detail = "In the backlog, not on the board. Import would create it.",
            });
        }

        foreach (var work in boardEpics.Values.Where(e => !matchedEpicIds.Contains(e.Id)).OrderBy(e => e.Id))
        {
            findings.Add(new AuditFinding
            {
                Kind = AuditKind.Extra,
                Level = BacklogLevel.Epic,
                Title = work.Title.Trim(),
                BoardId = work.Id,
                Detail = "On the board, with no Epic heading in the backlog.",
            });
        }

        foreach (var code in wantIssues.Keys.Except(boardIssues.Keys).Order(StringComparer.Ordinal))
        {
            findings.Add(new AuditFinding
            {
                Kind = AuditKind.Missing,
                Level = BacklogLevel.Issue,
                Code = code,
                Title = wantIssues[code].Title,
                Detail = "In the backlog, not on the board. Import would create it.",
            });
        }

        foreach (var code in boardIssues.Keys.Except(wantIssues.Keys).Order(StringComparer.Ordinal))
        {
            findings.Add(new AuditFinding
            {
                Kind = AuditKind.Extra,
                Level = BacklogLevel.Issue,
                Code = code,
                Title = boardIssues[code].Title.Trim(),
                BoardId = boardIssues[code].Id,
                Detail = "On the board, with no matching heading in the backlog.",
            });
        }

        foreach (var code in wantIssues.Keys.Intersect(boardIssues.Keys).Order(StringComparer.Ordinal))
        {
            var want = wantIssues[code];
            var work = boardIssues[code];
            var boardTitle = work.Title.Trim();

            if (!string.Equals(want.Title, boardTitle, StringComparison.Ordinal))
            {
                findings.Add(new AuditFinding
                {
                    Kind = AuditKind.TitleDrift,
                    Level = BacklogLevel.Issue,
                    Code = code,
                    Title = want.Title,
                    BoardId = work.Id,
                    Detail = $"Board reads \"{boardTitle}\"; the backlog reads \"{want.Title}\".",
                });
            }

            // Normalised, so an HTML artefact the board added itself is not
            // mistaken for real drift and reported on every run.
            if (MarkdownHtml.Normalize(want.Html) != MarkdownHtml.Normalize(work.Description))
            {
                findings.Add(new AuditFinding
                {
                    Kind = AuditKind.DescriptionDrift,
                    Level = BacklogLevel.Issue,
                    Code = code,
                    Title = want.Title,
                    BoardId = work.Id,
                    Detail = "The description on the board differs from the backlog. Resync would rewrite it.",
                });
            }
        }

        // Task parity, per Issue, against the backlog's top-level bullets.
        var childrenByParent = ChildrenByParent(snapshot);
        var tasksChecked = 0;
        foreach (var code in bulletsByCode.Keys.Intersect(boardIssues.Keys).Order(StringComparer.Ordinal))
        {
            var issue = boardIssues[code];
            var desired = bulletsByCode[code]
                .Select(b => Truncate(MarkdownHtml.Plain(b), config.TaskTitleMax))
                .ToHashSet(StringComparer.Ordinal);

            var actual = childrenByParent.GetValueOrDefault(issue.Id, [])
                .Where(c => c.WorkItemType == taskType)
                .Select(c => c.Title)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var missing in desired.Except(actual).Order(StringComparer.Ordinal))
            {
                findings.Add(new AuditFinding
                {
                    Kind = AuditKind.MissingTask,
                    Level = BacklogLevel.Issue,
                    Code = code,
                    Title = missing,
                    BoardId = issue.Id,
                    Detail = $"Bullet under {code} with no child Task. Resync tasks would create it.",
                });
            }

            foreach (var extra in actual.Except(desired).Order(StringComparer.Ordinal))
            {
                findings.Add(new AuditFinding
                {
                    Kind = AuditKind.StrayTask,
                    Level = BacklogLevel.Issue,
                    Code = code,
                    Title = extra,
                    BoardId = issue.Id,
                    Detail = $"Child Task of {code} with no matching bullet. Resync tasks would delete it.",
                });
            }

            tasksChecked++;
        }

        // State never comes from the backlog — it has no state column — so this
        // checks the hierarchy against itself. Azure DevOps propagates state
        // upward but never downward, so a closed Epic can sit above open Issues
        // and Tasks indefinitely.
        var (downward, upward) = StateDrift(snapshot, childrenByParent, done);

        foreach (var ancestor in downward.Values.Distinct().Order())
        {
            var openIds = downward.Where(d => d.Value == ancestor).Select(d => d.Key).Order().ToArray();
            var parent = snapshot.Items.First(i => i.Id == ancestor);
            findings.Add(new AuditFinding
            {
                Kind = AuditKind.OpenDescendantOfDone,
                Level = parent.WorkItemType == epicType ? BacklogLevel.Epic : BacklogLevel.Issue,
                Code = config.IssueCodeRegex.Match(parent.Title) is { Success: true } m
                    ? m.Groups[1].Value.ToUpperInvariant()
                    : null,
                Title = parent.Title.Trim(),
                BoardId = ancestor,
                BoardIds = openIds,
                Detail = $"{parent.WorkItemType} #{ancestor} is {done} with {openIds.Length} open descendant(s). Close children would close them.",
            });
        }

        var reviews = upward
            .Order()
            .Select(id => snapshot.Items.First(i => i.Id == id))
            .Select(work => new AuditFinding
            {
                Kind = AuditKind.EveryChildDone,
                Level = work.WorkItemType == epicType ? BacklogLevel.Epic : BacklogLevel.Issue,
                Title = work.Title.Trim(),
                BoardId = work.Id,
                Detail = $"Every child is {done} while #{work.Id} is {work.State}. A judgement call, not a defect: a parent can hold sign-off work of its own.",
            })
            .ToArray();

        return new AuditReport
        {
            Findings = findings,
            Reviews = reviews,
            BoardFingerprint = snapshot.Fingerprint,
            BacklogFingerprint = FingerprintBacklog(backlogMarkdown),
            BoardEpicCount = boardEpics.Count,
            BacklogEpicCount = wantEpics.Count,
            BoardIssueCount = boardIssues.Count,
            BacklogIssueCount = wantIssues.Count,
            IssuesTaskChecked = tasksChecked,
        };
    }

    /// <summary>
    /// The CLI matches an Epic by substring in both directions, so a board Epic
    /// whose title is a prefix of the backlog's still matches rather than reading
    /// as two different Epics. Import relies on the same rule, and audit must
    /// agree with import or it reports drift import would not fix.
    /// </summary>
    private static int? MatchEpic(Dictionary<int, BoardWorkItem> boardEpics, string wantedLowerTitle)
    {
        // Ordered by id so the fallback picks the same Epic on every run.
        foreach (var work in boardEpics.Values.OrderBy(e => e.Id))
        {
            var key = work.Title.Trim().ToLowerInvariant();
            if (key == wantedLowerTitle ||
                key.Contains(wantedLowerTitle, StringComparison.Ordinal) ||
                wantedLowerTitle.Contains(key, StringComparison.Ordinal))
            {
                return work.Id;
            }
        }

        return null;
    }

    /// <summary>Every item's children, by parent id, from the one snapshot.</summary>
    internal static Dictionary<int, List<BoardWorkItem>> ChildrenByParent(BoardSnapshot snapshot)
    {
        var present = snapshot.Items.ToDictionary(i => i.Id);
        var children = new Dictionary<int, List<BoardWorkItem>>();
        foreach (var work in snapshot.Items.OrderBy(i => i.Id))
        {
            if (work.ParentId is { } parent && present.ContainsKey(parent))
            {
                if (!children.TryGetValue(parent, out var list))
                {
                    list = [];
                    children[parent] = list;
                }

                list.Add(work);
            }
        }

        return children;
    }

    /// <summary>
    /// The two ways a board's states can disagree with its hierarchy.
    /// <c>downward</c> maps each open item to the nearest ancestor already done —
    /// closing an Epic silently leaves its Issues and Tasks open. <c>upward</c>
    /// lists parents whose children are all done while the parent is not.
    /// </summary>
    internal static (Dictionary<int, int> Downward, List<int> Upward) StateDrift(
        BoardSnapshot snapshot,
        Dictionary<int, List<BoardWorkItem>> childrenByParent,
        string done)
    {
        var byId = snapshot.Items.ToDictionary(i => i.Id);

        var downward = new Dictionary<int, int>();
        foreach (var work in snapshot.Items.OrderBy(i => i.Id))
        {
            if (string.Equals(work.State, done, StringComparison.Ordinal))
            {
                continue;
            }

            if (NearestDoneAncestor(byId, work.Id, done) is { } ancestor)
            {
                downward[work.Id] = ancestor;
            }
        }

        var upward = childrenByParent
            .Where(entry =>
                byId.TryGetValue(entry.Key, out var parent) &&
                !string.Equals(parent.State, done, StringComparison.Ordinal) &&
                entry.Value.Count > 0 &&
                entry.Value.All(c => string.Equals(c.State, done, StringComparison.Ordinal)))
            .Select(entry => entry.Key)
            .Order()
            .ToList();

        return (downward, upward);
    }

    private static int? NearestDoneAncestor(Dictionary<int, BoardWorkItem> byId, int id, string done)
    {
        var parent = byId[id].ParentId;

        // A malformed board could describe a parent cycle; walking it would hang
        // the Plan rather than report anything, so the walk remembers where it has been.
        var seen = new HashSet<int> { id };
        while (parent is { } current && byId.TryGetValue(current, out var work) && seen.Add(current))
        {
            if (string.Equals(work.State, done, StringComparison.Ordinal))
            {
                return current;
            }

            parent = work.ParentId;
        }

        return null;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    private static void Add(Dictionary<string, List<int>> map, string key, int id)
    {
        if (!map.TryGetValue(key, out var ids))
        {
            ids = [];
            map[key] = ids;
        }

        ids.Add(id);
    }
}
