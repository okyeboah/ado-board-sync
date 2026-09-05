using AdoBoardSync.Core.Backlog;
using AdoBoardSync.Core.Configuration;
using AdoBoardSync.Core.Diff;
using AdoBoardSync.Core.Markdown;
using AdoBoardSync.Core.Results;

namespace AdoBoardSync.Core.Agents;

/// <summary>
/// An agent's edit, re-parsed and re-validated, ready to be shown as a diff and
/// accepted or rejected whole (ABSD-704).
///
/// Building one is the gate: <see cref="Build" /> refuses an edit the parser cannot
/// make sense of, so a backlog that would break the editor or the Plan Builder is
/// never shown as something a reviewer can accept. A refusal is not silent — the
/// caller puts the file back and reports the code.
///
/// <see cref="Items" /> and <see cref="MarkupProblemCount" /> come from that same
/// validation pass, so accepting hands the editor a parse that has already been
/// done rather than one it has to trust.
/// </summary>
public sealed record AgentEditReview
{
    public required AgentEditSnapshot Snapshot { get; init; }

    public required string OriginalText { get; init; }

    public required string RevisedText { get; init; }

    public required TextDiffResult Diff { get; init; }

    public required IReadOnlyList<BacklogItem> Items { get; init; }

    public required int MarkupProblemCount { get; init; }

    public bool HasChanges => Diff.HasChanges;

    /// <summary>
    /// Re-parses and re-validates an agent's bytes against the file as it was.
    ///
    /// The three refusals are the ones that would otherwise land somewhere worse
    /// than here:
    ///
    /// <list type="bullet">
    /// <item>Not UTF-8 — the file store refuses to read it on the next open, leaving
    /// a profile that cannot be re-opened.</item>
    /// <item>Nothing parses out of a backlog that used to have items — every surface
    /// would show an empty tree, and an Import Plan built from it would propose
    /// nothing while the board still holds the work.</item>
    /// <item>A repeated Issue code — <see cref="BacklogParser.TasksByCode" /> keys a
    /// dictionary by code and throws on a duplicate, so this would surface as a
    /// crash inside a Plan rather than as a problem with the edit.</item>
    /// </list>
    ///
    /// Markup problems are counted, not refused, unless the agent introduced them: a
    /// backlog that already had malformed markup is one a user may well be asking an
    /// agent to fix, and refusing that edit would make the problem permanent. Apply's
    /// own markup gate still blocks a write either way (PRD-AC-03).
    /// </summary>
    public static Result<AgentEditReview> Build(
        BoardConfig config,
        AgentEditSnapshot snapshot,
        string originalText,
        byte[] revisedBytes)
    {
        var decoded = AgentEditSnapshot.Decode(revisedBytes, snapshot.Path);
        if (decoded.IsFailure)
        {
            return decoded.Error!;
        }

        var revisedText = decoded.Value;
        var items = BacklogParser.Parse(config, revisedText);
        var originalItems = BacklogParser.Parse(config, originalText);

        if (items.Count == 0 && originalItems.Count > 0)
        {
            return Error.Validation(
                "agent.edit.unparseable",
                $"The edit leaves no Epics or Issues the parser can find in {snapshot.Path}, where there "
                + $"were {originalItems.Count} before. The edit was not shown, and the file was put back as it was.");
        }

        if (FirstRepeatedCode(items) is { } repeated)
        {
            return Error.Validation(
                "agent.edit.duplicate_code",
                $"The edit gives {repeated} to more than one Issue. An Issue code is how a backlog item is "
                + "matched to its work item, so it has to be unique. The edit was not shown, and the file "
                + "was put back as it was.");
        }

        var problems = BacklogMarkupAudit.Total(items);
        var problemsBefore = BacklogMarkupAudit.Total(originalItems);
        if (problems > problemsBefore)
        {
            return Error.Validation(
                "agent.edit.markup_invalid",
                $"The edit introduces {problems - problemsBefore} markup problem(s) — check-html would fail "
                + "on it. The edit was not shown, and the file was put back as it was.");
        }

        return new AgentEditReview
        {
            Snapshot = snapshot,
            OriginalText = originalText,
            RevisedText = revisedText,
            Diff = TextDiff.Between(originalText, revisedText),
            Items = items,
            MarkupProblemCount = problems,
        };
    }

    private static string? FirstRepeatedCode(IReadOnlyList<BacklogItem> items)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item.Code is { } code && !seen.Add(code))
            {
                return code;
            }
        }

        return null;
    }
}
