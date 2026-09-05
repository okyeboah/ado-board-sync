using System.Security.Cryptography;
using System.Text;
using AdoBoardSync.Core.Backlog;
using AdoBoardSync.Core.Board;
using AdoBoardSync.Core.Configuration;
using AdoBoardSync.Core.Markdown;

namespace AdoBoardSync.Core.Planning;

/// <summary>
/// Computes what Apply would do, without touching Azure DevOps. Every method is
/// pure — backlog items in, board snapshot in, Plan out — which is what keeps
/// generation read-only (ARCHITECTURE.md §5.2).
///
/// The rules are ported from the CLI: <c>import_items</c> for
/// <see cref="BuildImport"/>, <c>resync</c> for <see cref="BuildResync"/>, and
/// <c>resync_tasks</c> for <see cref="BuildResyncTasks"/>. One deliberate
/// divergence: the CLI's import reads the intermediate CSV, this reads the parsed
/// backlog directly, so a stale CSV cannot produce a wrong Plan.
/// </summary>
/// <remarks>
/// Split across three files by command family, because one file holding every
/// command passed the 500-line limit: this one keeps the structural reconciles
/// (import, resync, resync-tasks), <c>PlanBuilder.Lifecycle.cs</c> holds the
/// ownership/scheduling/state commands, and <c>PlanBuilder.Audit.cs</c> the
/// read-only audit.
/// </remarks>
public static partial class PlanBuilder
{
    /// <summary>Fingerprints the backlog text, for the stale-plan guard.</summary>
    public static string FingerprintBacklog(string markdown) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(markdown)));

    /// <summary>
    /// Plans the Epics and Issues the board does not have yet. Import never updates
    /// and never deletes: an item already on the board is an Unchanged row.
    /// </summary>
    public static Plan BuildImport(
        BoardConfig config,
        IReadOnlyList<BacklogItem> items,
        BoardSnapshot snapshot,
        string backlogMarkdown)
    {
        var epicType = config.Types["epic"];
        var storyType = config.Types["story"];

        // Ordered by board id so the substring fallback below picks the same Epic
        // on every run, rather than leaving it to hash order.
        var boardEpics = snapshot.Items
            .Where(i => i.WorkItemType == epicType)
            .OrderBy(i => i.Id)
            .Select(i => (Key: i.Title.Trim().ToLowerInvariant(), i.Id))
            .ToArray();

        var epicByTitle = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (key, id) in boardEpics)
        {
            epicByTitle.TryAdd(key, id);
        }

        var existingIssues = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in snapshot.Items.Where(i => i.WorkItemType == storyType).OrderBy(i => i.Id))
        {
            var title = item.Title.Trim();
            var match = config.IssueCodeRegex.Match(title);
            if (match.Success)
            {
                existingIssues.TryAdd(match.Groups[1].Value.ToUpperInvariant(), item.Id);
            }

            existingIssues.TryAdd(title.ToLowerInvariant(), item.Id);
        }

        var rows = new List<PlanRow>();
        var planned = new HashSet<string>(StringComparer.Ordinal);

        int? lastEpicId = null;
        string? lastEpicTitle = null;

        foreach (var item in items)
        {
            var title = item.Title.Trim();
            var html = MarkdownHtml.ToHtml(item.DescriptionLines);

            if (item.Level == BacklogLevel.Epic)
            {
                var key = title.ToLowerInvariant();
                int? found = epicByTitle.TryGetValue(key, out var exact) ? exact : null;

                if (found is null)
                {
                    // The CLI matches an Epic by substring, so a board Epic whose
                    // title is a prefix of the backlog's still matches rather than
                    // creating a second Epic beside it.
                    foreach (var (boardKey, id) in boardEpics)
                    {
                        if (boardKey.Contains(key, StringComparison.Ordinal) ||
                            key.Contains(boardKey, StringComparison.Ordinal))
                        {
                            found = id;
                            break;
                        }
                    }
                }

                if (found is not null)
                {
                    lastEpicId = found;
                    lastEpicTitle = null;
                    rows.Add(new PlanRow
                    {
                        Operation = PlanOperation.Unchanged,
                        Level = BacklogLevel.Epic,
                        Title = title,
                        BoardId = found,
                        DescriptionHtml = html,
                    });
                    continue;
                }

                lastEpicId = null;
                lastEpicTitle = title;

                if (planned.Add($"epic{key}"))
                {
                    rows.Add(new PlanRow
                    {
                        Operation = PlanOperation.Create,
                        Level = BacklogLevel.Epic,
                        Title = title,
                        DescriptionHtml = html,
                    });
                }

                continue;
            }

            var codeMatch = config.IssueCodeRegex.Match(title);
            var code = codeMatch.Success ? codeMatch.Groups[1].Value.ToUpperInvariant() : null;

            if ((code is not null && existingIssues.TryGetValue(code, out var byCode)) ||
                existingIssues.TryGetValue(title.ToLowerInvariant(), out byCode))
            {
                rows.Add(new PlanRow
                {
                    Operation = PlanOperation.Unchanged,
                    Level = BacklogLevel.Issue,
                    Title = title,
                    Code = code,
                    BoardId = byCode,
                    DescriptionHtml = html,
                });
                continue;
            }

            // Two backlog rows carrying one code must not become two work items.
            if (!planned.Add($"issue{code ?? title.ToLowerInvariant()}"))
            {
                continue;
            }

            rows.Add(new PlanRow
            {
                Operation = PlanOperation.Create,
                Level = BacklogLevel.Issue,
                Title = title,
                Code = code,
                ParentBoardId = lastEpicId,
                ParentTitle = lastEpicTitle,
                DescriptionHtml = html,
            });
        }

        return new Plan
        {
            Command = PlanCommand.Import,
            Rows = rows,
            BacklogFingerprint = FingerprintBacklog(backlogMarkdown),
            BoardFingerprint = snapshot.Fingerprint,
        };
    }

    /// <summary>
    /// Plans the title and description writes that bring the board back in line
    /// with the backlog. Resync never creates and never deletes.
    /// </summary>
    public static Plan BuildResync(
        BoardConfig config,
        IReadOnlyList<BacklogItem> items,
        BoardSnapshot snapshot,
        string backlogMarkdown)
    {
        var epicType = config.Types["epic"];
        var storyType = config.Types["story"];

        var wantEpics = new List<(string Key, string Html)>();
        var wantIssues = new Dictionary<string, (string Title, string Html)>(StringComparer.Ordinal);

        foreach (var item in items)
        {
            var html = MarkdownHtml.ToHtml(item.DescriptionLines);
            if (item.Level == BacklogLevel.Epic)
            {
                wantEpics.Add((item.Title.Trim().ToLowerInvariant(), html));
            }
            else if (item.Code is { Length: > 0 } code)
            {
                wantIssues[code.ToUpperInvariant()] = (item.Title.Trim(), html);
            }
        }

        var rows = new List<PlanRow>();

        foreach (var work in snapshot.Items.OrderBy(i => i.Id))
        {
            var title = work.Title.Trim();

            if (work.WorkItemType == epicType)
            {
                var lowered = title.ToLowerInvariant();
                string? wanted = null;
                foreach (var (key, html) in wantEpics)
                {
                    if (key.Contains(lowered, StringComparison.Ordinal) ||
                        lowered.Contains(key, StringComparison.Ordinal))
                    {
                        wanted = html;
                        break;
                    }
                }

                if (wanted is null)
                {
                    continue;
                }

                // Compared normalised, so an HTML artefact the board added itself is
                // not mistaken for a real difference and rewritten on every run.
                if (MarkdownHtml.Normalize(wanted) == MarkdownHtml.Normalize(work.Description))
                {
                    rows.Add(Unchanged(BacklogLevel.Epic, title, null, work.Id, wanted));
                    continue;
                }

                rows.Add(new PlanRow
                {
                    Operation = PlanOperation.Update,
                    Level = BacklogLevel.Epic,
                    Title = title,
                    BoardId = work.Id,
                    DescriptionHtml = wanted,
                    Changes =
                    [
                        new PlanFieldChange(BoardFieldChange.DescriptionField, work.Description, wanted),
                    ],
                });
                continue;
            }

            if (work.WorkItemType != storyType)
            {
                continue;
            }

            var match = config.IssueCodeRegex.Match(title);
            if (!match.Success)
            {
                continue;
            }

            var code = match.Groups[1].Value.ToUpperInvariant();
            if (!wantIssues.TryGetValue(code, out var want))
            {
                continue;
            }

            var changes = new List<PlanFieldChange>();
            if (title != want.Title)
            {
                changes.Add(new PlanFieldChange(BoardFieldChange.TitleField, title, want.Title));
            }

            if (MarkdownHtml.Normalize(work.Description) != MarkdownHtml.Normalize(want.Html))
            {
                changes.Add(new PlanFieldChange(BoardFieldChange.DescriptionField, work.Description, want.Html));
            }

            if (changes.Count == 0)
            {
                rows.Add(Unchanged(BacklogLevel.Issue, title, code, work.Id, want.Html));
                continue;
            }

            rows.Add(new PlanRow
            {
                Operation = PlanOperation.Update,
                Level = BacklogLevel.Issue,
                Title = want.Title,
                Code = code,
                BoardId = work.Id,
                DescriptionHtml = want.Html,
                Changes = changes,
            });
        }

        return new Plan
        {
            Command = PlanCommand.Resync,
            Rows = rows,
            BacklogFingerprint = FingerprintBacklog(backlogMarkdown),
            BoardFingerprint = snapshot.Fingerprint,
        };
    }

    private static PlanRow Unchanged(BacklogLevel level, string title, string? code, int id, string html) =>
        new()
        {
            Operation = PlanOperation.Unchanged,
            Level = level,
            Title = title,
            Code = code,
            BoardId = id,
            DescriptionHtml = html,
        };

    /// <summary>
    /// Plans each Issue's child Tasks against its backlog bullets: create the
    /// missing, delete the stray. Never touches Epics, Issues, or fields.
    ///
    /// Titles are compared the way the CLI compares them: a wanted Task's title is
    /// its bullet converted to plain text and cut at <c>task_title_max</c>; an
    /// existing Task survives when its stored title equals one of those keys. The
    /// board read already carried every Task's parent id on the batched get, so no
    /// per-Issue relations lookup happens here either.
    /// </summary>
    public static Plan BuildResyncTasks(
        BoardConfig config,
        IReadOnlyList<BacklogItem> items,
        BoardSnapshot snapshot,
        string backlogMarkdown)
    {
        var storyType = config.Types["story"];
        var taskType = config.Types["task"];
        var taskTitleMax = config.TaskTitleMax;

        // Backlog document order — Issues as written, the same sequence the CLI walks.
        var bulletsByCode = new List<(string Code, IReadOnlyList<string> Bullets)>();
        foreach (var item in items)
        {
            if (item.Level == BacklogLevel.Issue && item.Code is { Length: > 0 } code &&
                item.Bullets.Count > 0)
            {
                bulletsByCode.Add((code.ToUpperInvariant(), item.Bullets));
            }
        }

        var issueIdsByCode = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var work in snapshot.Items)
        {
            if (work.WorkItemType != storyType)
            {
                continue;
            }

            var match = config.IssueCodeRegex.Match(work.Title);
            if (match.Success)
            {
                issueIdsByCode.TryAdd(match.Groups[1].Value.ToUpperInvariant(), work.Id);
            }
        }

        var boardIssueIds = new HashSet<int>(issueIdsByCode.Values);
        var existingByParent = new Dictionary<int, List<(int Id, string Title)>>();
        foreach (var work in snapshot.Items)
        {
            if (work.WorkItemType == taskType && work.ParentId is { } parent &&
                boardIssueIds.Contains(parent))
            {
                if (!existingByParent.TryGetValue(parent, out var children))
                {
                    children = [];
                    existingByParent[parent] = children;
                }

                children.Add((work.Id, work.Title));
            }
        }

        var rows = new List<PlanRow>();
        foreach (var (code, bullets) in bulletsByCode)
        {
            if (!issueIdsByCode.TryGetValue(code, out var issueId))
            {
                continue;   // No board Issue for this code — nothing to hang Tasks off.
            }

            // Last bullet wins a duplicated title, matching the CLI's dict build.
            var desired = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var bullet in bullets)
            {
                var plain = MarkdownHtml.Plain(bullet);
                desired[plain[..Math.Min(taskTitleMax, plain.Length)]] = bullet;
            }

            var existing = existingByParent.GetValueOrDefault(issueId) ?? [];

            foreach (var (title, bullet) in desired)
            {
                if (existing.All(e => e.Title != title))
                {
                    rows.Add(new PlanRow
                    {
                        Operation = PlanOperation.Create,
                        Level = BacklogLevel.Issue,
                        Title = title,
                        Code = code,
                        ParentBoardId = issueId,
                        DescriptionHtml = MarkdownHtml.Inline(bullet),
                    });
                }
            }

            foreach (var (id, title) in existing)
            {
                if (!desired.ContainsKey(title))
                {
                    rows.Add(new PlanRow
                    {
                        Operation = PlanOperation.Delete,
                        Level = BacklogLevel.Issue,
                        Title = title,
                        Code = code,
                        BoardId = id,
                    });
                }
            }
        }

        return new Plan
        {
            Command = PlanCommand.ResyncTasks,
            Rows = rows,
            BacklogFingerprint = FingerprintBacklog(backlogMarkdown),
            BoardFingerprint = snapshot.Fingerprint,
        };
    }
}
