using AdoBoardSync.Core.Diagnostics;
using AdoBoardSync.Core.Operations;
using AdoBoardSync.Core.Planning;

namespace AdoBoardSync.Desktop.Services;

/// <summary>
///     Records one Apply run in the operation history as it happens (ABSD-501).
///
///     It reports its own failures to diagnostics but does not narrate the Apply.
///     The started/finished/row-failed events it wrote by hand duplicated
///     DiagnosticsExtensions in a second vocabulary; the Plan gate emits those now
///     (ABSD-507). What remains here is what only this class knows: that the
///     history store would not take a write.
///
///     It sits between the Plan gate and the store rather than inside either,
///     because recording must not be able to fail the write it is recording. Every
///     method here swallows a store failure into diagnostics and returns: a history
///     that cannot be written is a support problem, while an Apply that aborts
///     because its audit trail is unavailable is a correctness problem, and the
///     second is much worse than the first.
///
///     The run is opened before the first write and completed after the last, so a
///     crash mid-Apply leaves an open run — which is exactly what a crash should
///     look like afterwards, rather than no record at all.
/// </summary>
public sealed class ApplyHistoryRecorder(IOperationHistory history, IDiagnostics? diagnostics = null)
{
    private readonly IDiagnostics _diagnostics = diagnostics ?? NullDiagnostics.Instance;

    /// <summary>
    ///     Apply fans its writes out over worker tasks and reports each outcome
    ///     through <see cref="IProgress{T}" />, so two outcomes can arrive at once
    ///     on thread-pool threads. The counters and the sequence number are read
    ///     and written under this lock; without it a run could close claiming
    ///     totals that never matched what it wrote.
    /// </summary>
    private readonly Lock _gate = new();

    /// <summary>
    ///     The chain every store write is appended to. Recording is fire-and-forget
    ///     from the caller's point of view, but the writes still have to reach the
    ///     store in order and finish before the run is closed — a run completed
    ///     while its own outcomes were still in flight would read back missing rows.
    /// </summary>
    private Task _pending = Task.CompletedTask;

    private long? _runId;
    private int _sequence;
    private int _succeeded;
    private int _failed;

    /// <summary>True once a run is open and outcomes can be appended to it.</summary>
    public bool IsRecording
    {
        get
        {
            lock (_gate)
            {
                return _runId is not null;
            }
        }
    }

    /// <summary>Opens the run. Called before the first write reaches the board.</summary>
    public async Task BeginAsync(
        string profileKey, PlanCommand command, DateTimeOffset startedAt,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _sequence = 0;
            _succeeded = 0;
            _failed = 0;
            _pending = Task.CompletedTask;
        }

        var begun = await history.BeginRunAsync(
            profileKey, command.ToString(), startedAt, cancellationToken);

        if (begun.IsFailure)
        {
            lock (_gate)
            {
                _runId = null;
            }

            Report("apply.history_unavailable", begun.Error!.SafeMessage);
            return;
        }

        lock (_gate)
        {
            _runId = begun.Value;
        }

    }

    /// <summary>Appends one row's outcome, in the reviewed Plan's own order.</summary>
    public async Task RecordAsync(ApplyOutcome outcome, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        long runId;
        int sequence;
        Task queued;

        lock (_gate)
        {
            if (outcome.Succeeded)
            {
                _succeeded++;
            }
            else
            {
                _failed++;
            }

            if (_runId is not { } open)
            {
                ReportUnrecorded(outcome, at);
                return;
            }

            runId = open;

            // The sequence is taken under the same lock that increments it, so two
            // outcomes arriving together cannot claim the same position in the
            // reviewed Plan's order.
            sequence = _sequence++;

            // Chained rather than run loose, so the writes reach the store in the
            // order they were sequenced and CompleteAsync has something to await.
            queued = _pending = Append(_pending);
        }

        await queued;
        return;

        async Task Append(Task previous)
        {
            await previous.ConfigureAwait(false);
            await WriteAsync(runId, sequence, outcome, at, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WriteAsync(
        long runId, int sequence, ApplyOutcome outcome, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var recorded = await history.RecordOutcomeAsync(
            runId,
            new OperationItemOutcome
            {
                RunId = runId,
                Sequence = sequence,
                Operation = outcome.Row.Operation.ToString(),
                Level = outcome.Row.Level.ToString(),
                Code = outcome.Row.Code,
                Title = outcome.Row.Title,
                BoardId = outcome.BoardId,
                Succeeded = outcome.Succeeded,
                Message = outcome.Message,
            },
            cancellationToken);

        if (recorded.IsFailure)
        {
            Report("apply.outcome_unrecorded", recorded.Error!.SafeMessage);
        }

        // A failed row is not reported here: the Plan gate's ApplyFinished names
        // every failed row by issue code. This event named it by title, which is
        // the user's own prose in a file they attach to support conversations.
    }

    /// <summary>Closes the run with its totals. A completed run never re-opens.</summary>
    public async Task CompleteAsync(
        string summary, DateTimeOffset finishedAt, CancellationToken cancellationToken = default)
    {
        long runId;
        int succeeded;
        int failed;
        Task pending;

        lock (_gate)
        {
            if (_runId is not { } open)
            {
                return;
            }

            runId = open;
            _runId = null;
            succeeded = _succeeded;
            failed = _failed;
            pending = _pending;
        }

        // Outcomes recorded fire-and-forget may still be in flight. Closing the
        // run before they land would read back as a completed run missing its own
        // rows — the store would be consistent and the record would still be a lie.
        await pending;

        var completed = await history.CompleteRunAsync(
            runId, finishedAt, succeeded, failed, summary, cancellationToken);

        if (completed.IsFailure)
        {
            Report("apply.run_unclosed", completed.Error!.SafeMessage);
        }

    }

    /// <summary>
    ///     Abandons the run without completing it — the Plan was refused before any
    ///     write. The row stays open in the store on purpose: "started and never
    ///     finished" is the honest record, and inventing a completion would say the
    ///     run ended cleanly when nothing ran at all.
    /// </summary>
    public void Abandon()
    {
        lock (_gate)
        {
            _runId = null;
        }
    }

    /// <summary>
    ///     An outcome that arrived with no run open — the store refused to open one,
    ///     or the run was abandoned. The totals are still counted, so the summary
    ///     stays right; only the per-row detail is lost, and diagnostics says so
    ///     rather than letting it vanish silently.
    /// </summary>
    private void ReportUnrecorded(ApplyOutcome outcome, DateTimeOffset at) =>
        _diagnostics.Write(new DiagnosticEvent
        {
            Timestamp = at,
            Level = DiagnosticLevel.Debug,
            Category = "apply",
            Code = "apply.no_open_run",
            // The code, not the title: a title is the user's own prose, and this
            // file is the one users are asked to attach to a support conversation.
            Message = $"Outcome not recorded, no run is open: {outcome.Row.Code}",
        });

    private void Report(string code, string message) =>
        _diagnostics.Write(new DiagnosticEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            Level = DiagnosticLevel.Warning,
            Category = "apply",
            Code = code,
            Message = $"The operation history could not be written: {message}",
        });
}
