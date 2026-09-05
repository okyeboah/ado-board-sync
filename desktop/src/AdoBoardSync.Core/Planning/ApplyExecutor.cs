using System.Collections.Concurrent;
using AdoBoardSync.Core.Backlog;
using AdoBoardSync.Core.Board;
using AdoBoardSync.Core.Configuration;
using AdoBoardSync.Core.Results;

namespace AdoBoardSync.Core.Planning;

/// <summary>What happened to one Plan row when Apply ran it.</summary>
public sealed record ApplyOutcome(PlanRow Row, bool Succeeded, int? BoardId, string Message);

/// <summary>The result of one Apply run, row by row.</summary>
public sealed record ApplyReport(IReadOnlyList<ApplyOutcome> Outcomes)
{
    public int Succeeded => Outcomes.Count(o => o.Succeeded);

    public int Failed => Outcomes.Count(o => !o.Succeeded);

    public bool AllSucceeded => Failed == 0;

    public string Summary => Failed == 0
        ? $"Applied {Succeeded} change{(Succeeded == 1 ? string.Empty : "s")}."
        : $"Applied {Succeeded}, failed {Failed}.";
}

/// <summary>
/// Executes a previously computed Plan and nothing else: no discovery, no
/// re-planning mid-run (ARCHITECTURE.md §5.3). Before the first write it checks
/// both fingerprints, and refuses the run if the backlog file or the board has
/// moved since.
///
/// Writes are fanned out over worker tasks — each targets its own work item, so
/// they cannot conflict — because a Plan of several hundred rows applied strictly
/// one-at-a-time spends most of its wall time waiting on round trips. Rows whose
/// results feed other rows wait: an Issue created under an Epic this run also
/// creates cannot start until that Epic's id exists. A wave therefore runs all
/// rows of one dependency level together, up to <see cref="MaxConcurrency"/> at
/// once, and the reported outcomes keep the Plan's row order regardless of which
/// write finished first.
/// </summary>
public static class ApplyExecutor
{
    public const int MaxConcurrency = 8;

    public static async Task<Result<ApplyReport>> ApplyAsync(
        IBoardGateway gateway,
        BoardConfig config,
        Plan plan,
        string currentBacklogFingerprint,
        string currentBoardFingerprint,
        IProgress<ApplyOutcome>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(plan.BacklogFingerprint, currentBacklogFingerprint, StringComparison.Ordinal))
        {
            return Error.Conflict(
                "plan.stale_backlog",
                "The backlog file changed after this Plan was generated. Generate it again so you approve what will actually be written.");
        }

        if (!string.Equals(plan.BoardFingerprint, currentBoardFingerprint, StringComparison.Ordinal))
        {
            return Error.Conflict(
                "plan.stale_board",
                "The board changed after this Plan was generated. Generate it again so you approve what will actually be written.");
        }

        var rows = plan.WriteRows;

        // An Epic created by this run parents the Issues that follow it. Depth is
        // the number of same-run ancestors a row waits on; everything else is 0.
        var depths = new int[rows.Count];
        var epicDepths = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);

        // A sprint Plan creates the iteration nodes it then moves work into. An
        // item cannot be assigned to an iteration path that does not exist yet, so
        // every node runs in the first wave and every item write in the second —
        // the same ordering the CLI gets for free by writing the nodes before it
        // starts patching.
        var hasIterationRows = rows.Any(r => r.Target == PlanTarget.IterationNode);

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];

            if (hasIterationRows)
            {
                depths[i] = row.Target == PlanTarget.IterationNode ? 0 : 1;
                continue;
            }

            if (row.Operation == PlanOperation.Create &&
                row.Level == BacklogLevel.Epic)
            {
                epicDepths[row.Title] = 0;
            }
            else if (row.Operation == PlanOperation.Create &&
                     row.ParentBoardId is null &&
                     row.ParentTitle is { } title &&
                     epicDepths.TryGetValue(title, out var parentDepth))
            {
                depths[i] = parentDepth + 1;
            }
        }

        var outcomes = new ApplyOutcome[rows.Count];

        foreach (var level in Enumerable.Range(0, depths.DefaultIfEmpty(0).Max() + 1))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var indexes = Enumerable.Range(0, rows.Count).Where(i => depths[i] == level).ToArray();
            if (indexes.Length == 0)
            {
                continue;
            }

            using var throttle = new SemaphoreSlim(Math.Min(MaxConcurrency, indexes.Length));
            var wave = indexes.Select(async index =>
            {
                await throttle.WaitAsync(cancellationToken);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    outcomes[index] = await RunAsync(gateway, config, plan.Command, rows[index], epicDepths, cancellationToken);
                }
                finally
                {
                    throttle.Release();
                }
            });

            await Task.WhenAll(wave);

            // Reported in the reviewed Plan's order, whatever order the writes landed in.
            if (progress is not null)
            {
                foreach (var index in indexes)
                {
                    progress.Report(outcomes[index]);
                }
            }
        }

        return new ApplyReport(outcomes);
    }

    private static async Task<ApplyOutcome> RunAsync(
        IBoardGateway gateway,
        BoardConfig config,
        PlanCommand command,
        PlanRow row,
        ConcurrentDictionary<string, int> createdEpics,
        CancellationToken cancellationToken)
    {
        if (row.Target == PlanTarget.IterationNode)
        {
            return await EnsureIterationAsync(gateway, config, row, cancellationToken);
        }

        return row.Operation switch
        {
            PlanOperation.Create => await CreateAsync(gateway, config, command, row, createdEpics, cancellationToken),
            PlanOperation.Delete => await DeleteAsync(gateway, config, row, cancellationToken),
            _ => await UpdateAsync(gateway, config, row, cancellationToken),
        };
    }

    /// <summary>
    /// Creates the sprint iteration node, then adds it to the team's selected
    /// sprints so it appears in that team's Sprints view. Both calls are idempotent
    /// by the connector's own contract, which is why an iteration row can read
    /// "Create" for a node that already exists without ever creating a second one.
    ///
    /// A node that is created but cannot be added to a team is reported as a
    /// success with the reason attached: the iteration exists and work can be
    /// assigned to it, which is what the rest of the Plan depends on. Failing the
    /// row here would strand every item write behind a cosmetic problem.
    /// </summary>
    private static async Task<ApplyOutcome> EnsureIterationAsync(
        IBoardGateway gateway,
        BoardConfig config,
        PlanRow row,
        CancellationToken cancellationToken)
    {
        if (row.Iteration is not { } iteration)
        {
            return new ApplyOutcome(row, false, null, "Iteration row carries no iteration to create.");
        }

        var ensured = await gateway.EnsureIterationAsync(
            config, iteration.Name, iteration.Start, iteration.Finish, cancellationToken);

        if (ensured.IsFailure)
        {
            return new ApplyOutcome(row, false, null, ensured.Error!.SafeMessage);
        }

        var node = ensured.Value;
        if (node.Identifier is not { Length: > 0 } identifier)
        {
            return new ApplyOutcome(row, true, null, $"{iteration.Name}: {node.Note} (no node id returned, so not added to a team)");
        }

        var team = config.Team;
        if (string.IsNullOrWhiteSpace(team))
        {
            var resolved = await gateway.DefaultTeamAsync(config, cancellationToken);
            team = resolved.IsFailure ? null : resolved.Value;
        }

        if (string.IsNullOrWhiteSpace(team))
        {
            return new ApplyOutcome(row, true, null,
                $"{iteration.Name}: {node.Note}; no team resolved, so it is not in a Sprints view");
        }

        var added = await gateway.AddTeamIterationAsync(config, team, identifier, cancellationToken);

        return added.IsFailure
            ? new ApplyOutcome(row, true, null, $"{iteration.Name}: {node.Note}; not added to '{team}' ({added.Error!.SafeMessage})")
            : new ApplyOutcome(row, true, null, $"{iteration.Name}: {node.Note}; in team '{team}'");
    }

    private static async Task<ApplyOutcome> CreateAsync(
        IBoardGateway gateway,
        BoardConfig config,
        PlanCommand command,
        PlanRow row,
        ConcurrentDictionary<string, int> createdEpics,
        CancellationToken cancellationToken)
    {
        var parentId = row.ParentBoardId;
        if (parentId is null && row.ParentTitle is { } parentTitle &&
            createdEpics.TryGetValue(parentTitle, out var justCreated))
        {
            parentId = justCreated;
        }

        // A row that names its own type wins: close-children and sprints touch
        // Tasks and Issues within one Plan, so the command alone cannot say which.
        var type = row.WorkItemType ?? (command == PlanCommand.ResyncTasks
            ? config.Types["task"]
            : row.Level == BacklogLevel.Epic ? config.Types["epic"] : config.Types["story"]);

        var created = await gateway.CreateAsync(
            config, type, row.Title, row.DescriptionHtml, parentId, cancellationToken);

        if (created.IsFailure)
        {
            return new ApplyOutcome(row, false, null, created.Error!.SafeMessage);
        }

        if (row.Level == BacklogLevel.Epic)
        {
            createdEpics[row.Title] = created.Value;
        }

        var parentNote = parentId is null ? string.Empty : $" (parent #{parentId})";

        // Fields the create endpoint does not take — sync-one's iteration path is
        // the only one today — are patched straight after. Two round trips rather
        // than one, and the item sits in the project root iteration for the moment
        // between them; widening CreateAsync into a field bag would buy back that
        // moment at the cost of every caller having to know the field vocabulary.
        var followUp = row.Changes
            .Where(c => c.Field != BoardFieldChange.TitleField && c.Field != BoardFieldChange.DescriptionField)
            .Select(c => new BoardFieldChange(c.Field, c.After))
            .ToArray();

        if (followUp.Length == 0)
        {
            return new ApplyOutcome(row, true, created.Value, $"Created #{created.Value}{parentNote}");
        }

        var patched = await gateway.UpdateAsync(config, created.Value, followUp, cancellationToken);

        return patched.IsFailure
            ? new ApplyOutcome(row, false, created.Value,
                $"Created #{created.Value}{parentNote}, but {row.ChangeSummary} did not stick: {patched.Error!.SafeMessage}")
            : new ApplyOutcome(row, true, created.Value,
                $"Created #{created.Value}{parentNote} ({row.ChangeSummary})");
    }

    private static async Task<ApplyOutcome> DeleteAsync(
        IBoardGateway gateway,
        BoardConfig config,
        PlanRow row,
        CancellationToken cancellationToken)
    {
        if (row.BoardId is not { } id)
        {
            return new ApplyOutcome(row, false, null, "Row has no board id to delete.");
        }

        var deleted = await gateway.DeleteAsync(config, id, cancellationToken);

        return deleted.IsFailure
            ? new ApplyOutcome(row, false, id, deleted.Error!.SafeMessage)
            : new ApplyOutcome(row, true, id, $"Deleted #{id}");
    }

    private static async Task<ApplyOutcome> UpdateAsync(
        IBoardGateway gateway,
        BoardConfig config,
        PlanRow row,
        CancellationToken cancellationToken)
    {
        if (row.BoardId is not { } id)
        {
            return new ApplyOutcome(row, false, null, "Row has no board id to update.");
        }

        IReadOnlyList<BoardFieldChange> changes =
            [.. row.Changes.Select(c => new BoardFieldChange(c.Field, c.After))];

        var updated = await gateway.UpdateAsync(config, id, changes, cancellationToken);

        return updated.IsFailure
            ? new ApplyOutcome(row, false, id, updated.Error!.SafeMessage)
            : new ApplyOutcome(row, true, id, $"Updated #{id} ({row.ChangeSummary})");
    }
}
