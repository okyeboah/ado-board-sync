using System.Globalization;
using AdoBoardSync.Core.Planning;
using AdoBoardSync.Core.Results;

namespace AdoBoardSync.Core.Diagnostics;

/// <summary>
/// The events ARCHITECTURE.md §7 asks for, each as one call (ABSD-507). They exist
/// so a call site records a Plan or an Apply without assembling a
/// <see cref="DiagnosticEvent"/> by hand — the field names have to match across
/// runs for a bundle to be filterable with <c>grep</c> or <c>jq</c>, and they will
/// not if every caller picks its own.
///
/// <para>
/// A failure is always reported through <see cref="OperationFailed"/> so it carries
/// the FSD §5.1 code the status bar showed the user. There is no second vocabulary
/// for logs: a support conversation and the user are describing the same event with
/// the same word.
/// </para>
///
/// <para>
/// <c>category</c> follows <see cref="DiagnosticEvent.Category"/>: the subsystem
/// that owns the operation — "plan", "apply", "backlog", "csv", "config".
/// </para>
/// </summary>
public static class DiagnosticsExtensions
{
    /// <summary>A Plan was computed. Records what it would do and how long deciding took.</summary>
    public static void PlanGenerated(this IDiagnostics diagnostics, Plan plan, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(plan);

        diagnostics.Write(new DiagnosticEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            Level = DiagnosticLevel.Info,
            Category = "plan",
            Message = $"Generated a {plan.Command} plan of {plan.Rows.Count} rows.",
            Data = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["command"] = plan.Command.ToString(),
                ["rows"] = Count(plan.Rows.Count),
                ["create"] = Count(plan.CreateCount),
                ["update"] = Count(plan.UpdateCount),
                ["delete"] = Count(plan.DeleteCount),
                ["unchanged"] = Count(plan.UnchangedCount),
                ["duration_ms"] = Milliseconds(duration),
            },
        });
    }

    /// <summary>
    /// Apply is about to write. Emitted before the first round trip on purpose: if
    /// the process dies mid-run this is the record that a write was in flight, which
    /// is the question asked after a partial Apply.
    /// </summary>
    public static void ApplyStarted(this IDiagnostics diagnostics, Plan plan)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(plan);

        diagnostics.Write(new DiagnosticEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            Level = DiagnosticLevel.Info,
            Category = "apply",
            Message = $"Applying {plan.WriteRows.Count} {plan.Command} changes.",
            Data = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["command"] = plan.Command.ToString(),
                ["write_rows"] = Count(plan.WriteRows.Count),
                ["backlog_fingerprint"] = plan.BacklogFingerprint,
                ["board_fingerprint"] = plan.BoardFingerprint,
            },
        });
    }

    /// <summary>
    /// Apply finished. Warning rather than Info when any row failed, so a bundle can
    /// be narrowed to the runs that went wrong without reading every line.
    ///
    /// The failed rows are named by issue code only. A title is the user's own text
    /// and a description is more so; neither belongs in a file the user will attach
    /// to a support conversation, and the code is what identifies the item anyway.
    /// </summary>
    public static void ApplyFinished(
        this IDiagnostics diagnostics, Plan plan, ApplyReport report, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(report);

        var failedCodes = report.Outcomes
            .Where(outcome => !outcome.Succeeded)
            .Select(outcome => outcome.Row.Code ?? outcome.Row.Level.ToString().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .Take(FailedCodeLimit)
            .ToList();

        diagnostics.Write(new DiagnosticEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            Level = report.AllSucceeded ? DiagnosticLevel.Info : DiagnosticLevel.Warning,
            Category = "apply",
            Message = report.Summary,
            Data = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["command"] = plan.Command.ToString(),
                ["succeeded"] = Count(report.Succeeded),
                ["failed"] = Count(report.Failed),
                ["duration_ms"] = Milliseconds(duration),
                ["failed_codes"] = string.Join(",", failedCodes),
            },
        });
    }

    /// <summary>
    /// A file reached disk. A refused or failed write is not this event — it is
    /// <see cref="OperationFailed"/> with the code that says why, so "the save did
    /// not happen" and "the save happened" never look alike in a bundle.
    /// </summary>
    public static void FileWritten(
        this IDiagnostics diagnostics, string category, string path, int byteCount)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        diagnostics.Write(new DiagnosticEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            Level = DiagnosticLevel.Info,
            Category = category,
            Message = $"Wrote {byteCount} bytes to {path}.",
            Data = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["path"] = path,
                ["bytes"] = Count(byteCount),
            },
        });
    }

    /// <summary>
    /// An operation failed, reported with the same FSD §5.1 code and the same safe
    /// message the user was shown. <see cref="Error.SafeMessage"/> is already the
    /// vetted wording; nothing here adds detail to it.
    /// </summary>
    public static void OperationFailed(this IDiagnostics diagnostics, string category, Error error)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(error);

        diagnostics.Write(new DiagnosticEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            Level = DiagnosticLevel.Error,
            Category = category,
            Code = error.Code,
            Message = error.SafeMessage,
            Data = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["kind"] = error.Kind.ToString(),
            },
        });
    }

    // Enough to see the shape of a bad run without turning one line into the whole
    // file when a board rejects every write at once.
    private const int FailedCodeLimit = 20;

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Milliseconds(TimeSpan duration) =>
        duration.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture);
}
