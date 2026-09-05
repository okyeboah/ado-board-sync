using System.Globalization;
using AdoBoardSync.Core.Agents;
using AdoBoardSync.Core.Operations;
using AdoBoardSync.Core.Results;
using Microsoft.Data.Sqlite;

namespace AdoBoardSync.Infrastructure.Operations;

/// <summary>
/// The local, append-only record of what this machine has done (ABSD-501) and of the
/// agent runs that led to it (ABSD-706). Both live in one store because they belong
/// to one timeline, so this adapter implements both ports and owns both sets of
/// tables. The agent tables are created from the first release: a schema that is
/// already there is one fewer migration to get right when ABSD-706 lands its writer.
///
/// Append-only in practice, not merely by convention. The only two statements that
/// touch an existing row are <see cref="CompleteRunAsync"/> closing an open run and
/// <see cref="RecordVerdictAsync"/> setting a verdict, and both are guarded in their
/// WHERE clause so a second attempt is refused with a typed conflict rather than
/// overwriting what the first one recorded. A history that can be rewritten is not
/// evidence.
/// </summary>
public sealed class SqliteOperationHistory : IOperationHistory, IAgentRunHistory, IDisposable, IAsyncDisposable
{
    private readonly HistoryDatabase _database;

    /// <param name="databasePath">Where the file lives. Defaults to the user's own
    /// local application data; tests pass a temporary path.</param>
    public SqliteOperationHistory(string? databasePath = null)
    {
        DatabasePath = databasePath ?? DefaultDatabasePath();
        _database = new HistoryDatabase(DatabasePath);
    }

    public string DatabasePath { get; }

    /// <summary>
    /// Under the user's own profile directory, never inside the repository or a
    /// profile's working directory: agent prompts are stored in this file, so it must
    /// not be somewhere a user commits or ships it by accident.
    /// </summary>
    public static string DefaultDatabasePath()
    {
        // Adopted rather than simply named: an installation that already has a
        // history.db under the old directory keeps it, moved across on first use.
        return LocalDataPaths.Adopted("history.db");
    }

    public Task<Result<long>> BeginRunAsync(
        string profileKey,
        string command,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(profileKey))
        {
            return Task.FromResult<Result<long>>(Error.Validation(
                "history.no_profile",
                "A run must name the profile it belongs to, or no timeline will ever show it."));
        }

        return _database.RunAsync<long>(async (connection, token) =>
        {
            await using var insert = NewCommand(connection, """
                INSERT INTO operation_run (profile_key, command, started_at, finished_at, succeeded, failed, summary)
                VALUES ($profile, $command, $startedAt, NULL, 0, 0, '');
                SELECT last_insert_rowid();
                """);

            Bind(insert, "$profile", profileKey);
            Bind(insert, "$command", command);
            Bind(insert, "$startedAt", HistoryTimestamp.ToText(startedAt));

            var id = await insert.ExecuteScalarAsync(token).ConfigureAwait(false);
            return Convert.ToInt64(id, CultureInfo.InvariantCulture);
        }, cancellationToken);
    }

    public Task<Result<bool>> RecordOutcomeAsync(
        long runId,
        OperationItemOutcome outcome,
        CancellationToken cancellationToken = default) =>
        _database.RunAsync<bool>(async (connection, token) =>
        {
            // The WHERE EXISTS is the append-only guard: an outcome can only join a
            // run that is still open, so nothing can be added to a run whose totals a
            // user has already been shown.
            await using var insert = NewCommand(connection, """
                INSERT INTO operation_item_outcome
                    (run_id, sequence, operation, level, code, title, board_id, succeeded, message)
                SELECT $runId, $sequence, $operation, $level, $code, $title, $boardId, $succeeded, $message
                WHERE EXISTS (SELECT 1 FROM operation_run WHERE id = $runId AND finished_at IS NULL);
                """);

            Bind(insert, "$runId", runId);
            Bind(insert, "$sequence", outcome.Sequence);
            Bind(insert, "$operation", outcome.Operation);
            Bind(insert, "$level", outcome.Level);
            Bind(insert, "$code", outcome.Code);
            Bind(insert, "$title", outcome.Title);
            Bind(insert, "$boardId", outcome.BoardId);
            Bind(insert, "$succeeded", outcome.Succeeded ? 1 : 0);
            Bind(insert, "$message", outcome.Message);

            var written = await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            if (written == 1)
            {
                return true;
            }

            return await ExplainRunRefusalAsync(
                connection,
                runId,
                $"Apply run #{runId} is already closed, so nothing further can be recorded against it.",
                token).ConfigureAwait(false);
        }, cancellationToken);

    public Task<Result<bool>> CompleteRunAsync(
        long runId,
        DateTimeOffset finishedAt,
        int succeeded,
        int failed,
        string summary,
        CancellationToken cancellationToken = default) =>
        _database.RunAsync<bool>(async (connection, token) =>
        {
            await using var update = NewCommand(connection, """
                UPDATE operation_run
                   SET finished_at = $finishedAt, succeeded = $succeeded, failed = $failed, summary = $summary
                 WHERE id = $runId AND finished_at IS NULL;
                """);

            Bind(update, "$finishedAt", HistoryTimestamp.ToText(finishedAt));
            Bind(update, "$succeeded", succeeded);
            Bind(update, "$failed", failed);
            Bind(update, "$summary", summary);
            Bind(update, "$runId", runId);

            var changed = await update.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            if (changed == 1)
            {
                return true;
            }

            return await ExplainRunRefusalAsync(
                connection,
                runId,
                $"Apply run #{runId} was already completed. A run closes once; a second completion would replace totals a user may already have read.",
                token).ConfigureAwait(false);
        }, cancellationToken);

    public Task<Result<IReadOnlyList<OperationRun>>> ListRunsAsync(
        string profileKey,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (InvalidLimit(limit) is { } refusal)
        {
            return Task.FromResult<Result<IReadOnlyList<OperationRun>>>(refusal);
        }

        return _database.RunAsync<IReadOnlyList<OperationRun>>(async (connection, token) =>
        {
            // started_at DESC is what ix_operation_run_timeline is shaped for; id DESC
            // only breaks ties, so two runs opened inside the same tick still read back
            // in a fixed order rather than whichever the page happened to hold first.
            await using var query = NewCommand(connection, """
                SELECT id, profile_key, command, started_at, finished_at, succeeded, failed, summary
                  FROM operation_run
                 WHERE profile_key = $profile
                 ORDER BY started_at DESC, id DESC
                 LIMIT $limit;
                """);

            Bind(query, "$profile", profileKey);
            Bind(query, "$limit", limit);

            var runs = new List<OperationRun>();
            await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                runs.Add(new OperationRun
                {
                    Id = reader.GetInt64(0),
                    ProfileKey = reader.GetString(1),
                    Command = reader.GetString(2),
                    StartedAt = HistoryTimestamp.FromText(reader.GetString(3)),
                    FinishedAt = reader.IsDBNull(4) ? null : HistoryTimestamp.FromText(reader.GetString(4)),
                    Succeeded = reader.GetInt32(5),
                    Failed = reader.GetInt32(6),
                    Summary = reader.GetString(7),
                });
            }

            return runs;
        }, cancellationToken);
    }

    public Task<Result<IReadOnlyList<OperationItemOutcome>>> ListOutcomesAsync(
        long runId,
        CancellationToken cancellationToken = default) =>
        _database.RunAsync<IReadOnlyList<OperationItemOutcome>>(async (connection, token) =>
        {
            // By sequence, not by id: Apply writes in parallel waves, so insertion
            // order is the order the network answered in. The user approved the Plan's
            // order, and that is the order the history has to read back in.
            await using var query = NewCommand(connection, """
                SELECT id, run_id, sequence, operation, level, code, title, board_id, succeeded, message
                  FROM operation_item_outcome
                 WHERE run_id = $runId
                 ORDER BY sequence, id;
                """);

            Bind(query, "$runId", runId);

            var outcomes = new List<OperationItemOutcome>();
            await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                outcomes.Add(new OperationItemOutcome
                {
                    Id = reader.GetInt64(0),
                    RunId = reader.GetInt64(1),
                    Sequence = reader.GetInt32(2),
                    Operation = reader.GetString(3),
                    Level = reader.GetString(4),
                    Code = reader.IsDBNull(5) ? null : reader.GetString(5),
                    Title = reader.GetString(6),
                    BoardId = reader.IsDBNull(7) ? null : reader.GetInt32(7),
                    Succeeded = reader.GetInt64(8) != 0,
                    Message = reader.GetString(9),
                });
            }

            return outcomes;
        }, cancellationToken);

    public Task<Result<long>> RecordRunAsync(AgentRunRecord record, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(record.ProfileKey))
        {
            return Task.FromResult<Result<long>>(Error.Validation(
                "history.no_profile",
                "An agent run must name the profile it belongs to, or no timeline will ever show it."));
        }

        // Null until ABSD-706's review pane sets it; SQLite has no boolean, so the
        // three states are 1, 0 and NULL rather than a sentinel.
        int? editAccepted = null;
        if (record.EditAccepted is { } verdict)
        {
            editAccepted = verdict ? 1 : 0;
        }

        return _database.RunAsync<long>(async (connection, token) =>
        {
            await using var insert = NewCommand(connection, """
                INSERT INTO agent_run
                    (profile_key, provider_id, provider_version, prompt, scope, scope_label,
                     started_at, finished_at, status, exit_code, edit_accepted, summary)
                VALUES ($profile, $providerId, $providerVersion, $prompt, $scope, $scopeLabel,
                        $startedAt, $finishedAt, $status, $exitCode, $editAccepted, $summary);
                SELECT last_insert_rowid();
                """);

            Bind(insert, "$profile", record.ProfileKey);
            Bind(insert, "$providerId", record.ProviderId);
            Bind(insert, "$providerVersion", record.ProviderVersion);
            Bind(insert, "$prompt", record.Prompt);
            Bind(insert, "$scope", record.Scope);
            Bind(insert, "$scopeLabel", record.ScopeLabel);
            Bind(insert, "$startedAt", HistoryTimestamp.ToText(record.StartedAt));
            Bind(insert, "$finishedAt", record.FinishedAt is { } finished ? HistoryTimestamp.ToText(finished) : null);
            Bind(insert, "$status", record.Status);
            Bind(insert, "$exitCode", record.ExitCode);
            Bind(insert, "$editAccepted", editAccepted);
            Bind(insert, "$summary", record.Summary);

            var id = await insert.ExecuteScalarAsync(token).ConfigureAwait(false);
            return Convert.ToInt64(id, CultureInfo.InvariantCulture);
        }, cancellationToken);
    }

    public Task<Result<bool>> RecordVerdictAsync(
        long runId,
        bool accepted,
        DateTimeOffset finishedAt,
        CancellationToken cancellationToken = default) =>
        _database.RunAsync<bool>(async (connection, token) =>
        {
            await using var update = NewCommand(connection, """
                UPDATE agent_run
                   SET edit_accepted = $accepted, finished_at = $finishedAt
                 WHERE id = $runId AND edit_accepted IS NULL;
                """);

            Bind(update, "$accepted", accepted ? 1 : 0);
            Bind(update, "$finishedAt", HistoryTimestamp.ToText(finishedAt));
            Bind(update, "$runId", runId);

            var changed = await update.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            if (changed == 1)
            {
                return true;
            }

            await using var probe = NewCommand(connection, "SELECT 1 FROM agent_run WHERE id = $runId;");
            Bind(probe, "$runId", runId);
            var exists = await probe.ExecuteScalarAsync(token).ConfigureAwait(false);

            return exists is null
                ? Error.NotFound("history.agent_run_not_found", $"No agent run #{runId} in the history.")
                : Error.Conflict(
                    "history.verdict_already_recorded",
                    $"Agent run #{runId} already carries a verdict. A diff is accepted or rejected once.");
        }, cancellationToken);

    /// <summary>
    /// The agent timeline. Named apart from <see cref="ListRunsAsync"/> because both
    /// ports declare the same parameters and differ only in return type, which C#
    /// cannot express as two public methods; the interface member below forwards here
    /// so a caller holding either port runs the same query.
    /// </summary>
    public Task<Result<IReadOnlyList<AgentRunRecord>>> ListAgentRunsAsync(
        string profileKey,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (InvalidLimit(limit) is { } refusal)
        {
            return Task.FromResult<Result<IReadOnlyList<AgentRunRecord>>>(refusal);
        }

        return _database.RunAsync<IReadOnlyList<AgentRunRecord>>(async (connection, token) =>
        {
            await using var query = NewCommand(connection, """
                SELECT id, profile_key, provider_id, provider_version, prompt, scope, scope_label,
                       started_at, finished_at, status, exit_code, edit_accepted, summary
                  FROM agent_run
                 WHERE profile_key = $profile
                 ORDER BY started_at DESC, id DESC
                 LIMIT $limit;
                """);

            Bind(query, "$profile", profileKey);
            Bind(query, "$limit", limit);

            var records = new List<AgentRunRecord>();
            await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                records.Add(new AgentRunRecord
                {
                    Id = reader.GetInt64(0),
                    ProfileKey = reader.GetString(1),
                    ProviderId = reader.GetString(2),
                    ProviderVersion = reader.GetString(3),
                    Prompt = reader.GetString(4),
                    Scope = reader.GetString(5),
                    ScopeLabel = reader.IsDBNull(6) ? null : reader.GetString(6),
                    StartedAt = HistoryTimestamp.FromText(reader.GetString(7)),
                    FinishedAt = reader.IsDBNull(8) ? null : HistoryTimestamp.FromText(reader.GetString(8)),
                    Status = reader.GetString(9),
                    ExitCode = reader.GetInt32(10),
                    EditAccepted = reader.IsDBNull(11) ? null : reader.GetInt64(11) != 0,
                    Summary = reader.GetString(12),
                });
            }

            return records;
        }, cancellationToken);
    }

    Task<Result<IReadOnlyList<AgentRunRecord>>> IAgentRunHistory.ListRunsAsync(
        string profileKey,
        int limit,
        CancellationToken cancellationToken) =>
        ListAgentRunsAsync(profileKey, limit, cancellationToken);

    public void Dispose() => _database.Dispose();

    public ValueTask DisposeAsync() => _database.DisposeAsync();

    private static Error? InvalidLimit(int limit) => limit > 0
        ? null
        : Error.Validation(
            "history.invalid_limit",
            $"A timeline page must ask for at least one run; {limit} was requested.");

    private static SqliteCommand NewCommand(SqliteConnection connection, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        return command;
    }

    private static void Bind(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static async Task<Result<bool>> ExplainRunRefusalAsync(
        SqliteConnection connection,
        long runId,
        string closedMessage,
        CancellationToken cancellationToken)
    {
        await using var probe = NewCommand(connection, "SELECT 1 FROM operation_run WHERE id = $runId;");
        Bind(probe, "$runId", runId);
        var exists = await probe.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return exists is null
            ? Error.NotFound("history.run_not_found", $"No Apply run #{runId} in the history.")
            : Error.Conflict("history.run_already_complete", closedMessage);
    }
}
