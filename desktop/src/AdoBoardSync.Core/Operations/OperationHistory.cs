using AdoBoardSync.Core.Results;

namespace AdoBoardSync.Core.Operations;

/// <summary>
/// One Apply run, as the history store holds it. Append-only: a run is opened
/// before the first write and completed after the last, and nothing rewrites it
/// afterwards — a history that can be edited is not evidence.
/// </summary>
public sealed record OperationRun
{
    public long Id { get; init; }

    /// <summary>
    /// Which Board profile the run belonged to. Every read is scoped by this, so
    /// no view can mix two profiles' runs (ABSD-502).
    /// </summary>
    public required string ProfileKey { get; init; }

    /// <summary>The Plan command this run applied, for example "Import".</summary>
    public required string Command { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>Null while the run is still open — including after a crash, which
    /// is exactly what an unfinished run should look like afterwards.</summary>
    public DateTimeOffset? FinishedAt { get; init; }

    public int Succeeded { get; init; }

    public int Failed { get; init; }

    public string Summary { get; init; } = string.Empty;

    public bool IsComplete => FinishedAt is not null;

    public bool AllSucceeded => IsComplete && Failed == 0;

    public int Total => Succeeded + Failed;
}

/// <summary>What one row of an applied Plan did, recorded as it happened.</summary>
public sealed record OperationItemOutcome
{
    public long Id { get; init; }

    public long RunId { get; init; }

    /// <summary>The row's position in the reviewed Plan, so the history reads in
    /// the order the user approved rather than the order the writes landed.</summary>
    public int Sequence { get; init; }

    /// <summary>Create, Update, Delete or Unchanged.</summary>
    public required string Operation { get; init; }

    /// <summary>Epic or Issue, as the Plan row carried it.</summary>
    public required string Level { get; init; }

    public string? Code { get; init; }

    public required string Title { get; init; }

    public int? BoardId { get; init; }

    public bool Succeeded { get; init; }

    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// The append-only local record of every Apply this machine has run (ABSD-501).
/// Declared in Core so the Plan/Apply surface depends on the contract rather than
/// on SQLite; the adapter lives in Infrastructure.
///
/// No method here takes or returns a credential: the store records what was done,
/// never what it was done with.
/// </summary>
public interface IOperationHistory
{
    /// <summary>Opens a run and returns its id. Called before the first write.</summary>
    Task<Result<long>> BeginRunAsync(
        string profileKey,
        string command,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken = default);

    /// <summary>Appends one row's outcome to an open run.</summary>
    Task<Result<bool>> RecordOutcomeAsync(
        long runId,
        OperationItemOutcome outcome,
        CancellationToken cancellationToken = default);

    /// <summary>Closes a run with its totals. A run never re-opens.</summary>
    Task<Result<bool>> CompleteRunAsync(
        long runId,
        DateTimeOffset finishedAt,
        int succeeded,
        int failed,
        string summary,
        CancellationToken cancellationToken = default);

    /// <summary>The most recent runs for one profile, newest first.</summary>
    Task<Result<IReadOnlyList<OperationRun>>> ListRunsAsync(
        string profileKey,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>Every recorded outcome of one run, in the Plan's own row order.</summary>
    Task<Result<IReadOnlyList<OperationItemOutcome>>> ListOutcomesAsync(
        long runId,
        CancellationToken cancellationToken = default);
}
