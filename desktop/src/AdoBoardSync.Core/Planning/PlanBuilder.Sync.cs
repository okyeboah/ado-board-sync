using AdoBoardSync.Core.Backlog;
using AdoBoardSync.Core.Board;
using AdoBoardSync.Core.Configuration;
using AdoBoardSync.Core.Markdown;
using AdoBoardSync.Core.Results;

namespace AdoBoardSync.Core.Planning;

/// <summary>
///     The CLI's <c>sync</c>: the structural reconcile chain — import, resync,
///     resync-tasks — planned as one reviewed write (FSD §3.3.4). The CLI runs
///     <c>gen-csv → check-html → import → resync → resync-tasks → audit</c>; here the
///     CSV stays its own export (FSD §3.9: an artifact, never a Plan input), the
///     markup gate runs before any Plan is shown, and the audit tail stays the
///     read-only Audit section.
/// </summary>
public static partial class PlanBuilder
{
    public static Result<Plan> BuildSync(
        BoardConfig config,
        IReadOnlyList<BacklogItem> items,
        BoardSnapshot snapshot,
        string backlogMarkdown)
    {
        // The CLI aborts the whole chain before any write when check-html fails;
        // a Plan that hides that behind a note would invite applying half a sync.
        var problems = BacklogMarkupAudit.Total(items);
        if (problems > 0)
            return Error.Validation(
                "markup.invalid",
                $"The backlog has {problems} markup problem(s). check-html would abort the "
                + "sync — fix them, then generate again.");

        var import = BuildImport(config, items, snapshot, backlogMarkdown);
        var resync = BuildResync(config, items, snapshot, backlogMarkdown);
        var tasks = BuildResyncTasks(config, items, snapshot, backlogMarkdown);

        var notes = new List<string>();
        foreach (var note in import.Notes.Concat(resync.Notes).Concat(tasks.Notes))
            if (!notes.Contains(note))
                notes.Add(note);

        notes.Add("Rows run in the chain's order: import, then resync, then the task "
                  + "reconcile. The CSV export and the audit stay their own sections.");

        return new Plan
        {
            Command = PlanCommand.Sync,
            Rows = [.. import.Rows, .. resync.Rows, .. tasks.Rows],
            Notes = notes,
            BacklogFingerprint = FingerprintBacklog(backlogMarkdown),
            BoardFingerprint = snapshot.Fingerprint
        };
    }
}