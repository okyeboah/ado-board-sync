using System.Globalization;
using AdoBoardSync.Core.Results;
using Microsoft.Data.Sqlite;

namespace AdoBoardSync.Infrastructure.Operations;

/// <summary>
/// The one SQLite file behind both histories: Apply runs with their per-item
/// outcomes (ABSD-501) and agent runs with their verdicts (ABSD-706). One file,
/// because the value of the timeline is that it interleaves the two — what an
/// agent wrote and what was then applied to the board are the same story.
///
/// Nothing in either schema holds a credential. The agent prompt is stored, because
/// ABSD-706 cannot show a user what an agent was asked to do without it — which
/// means a user who types a secret into a prompt has put that secret in this file.
/// That is why the database lives under the user's own profile directory and never
/// inside the repository or a profile's working directory: it must not be committed,
/// synced or exported by accident. The PAT itself, the contents of pat_file, and
/// every other token stay out of here entirely.
///
/// One owned connection per store rather than a pool: the log is low-traffic, and a
/// single handle means Dispose actually releases the file instead of parking it in a
/// pool the caller cannot reclaim. Contention between two stores, or two processes,
/// is left to SQLite's own WAL and busy timeout, which is what they are for.
/// </summary>
internal sealed class HistoryDatabase : IDisposable, IAsyncDisposable
{
    /// <summary>Written to <c>PRAGMA user_version</c> so a future migration has a
    /// number to branch on instead of having to sniff the tables for shape.</summary>
    internal const int SchemaVersion = 1;

    private const string Pragmas = """
        PRAGMA journal_mode = WAL;
        PRAGMA busy_timeout = 10000;
        PRAGMA foreign_keys = ON;
        """;

    // AUTOINCREMENT, not a bare rowid: SQLite reuses the highest rowid after a
    // delete, and a reused run id in an append-only log would silently attach one
    // run's outcomes to another. Nothing deletes today, so the cost is one counter
    // row and the guarantee outlives whatever gets added later.
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS operation_run (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            profile_key TEXT    NOT NULL,
            command     TEXT    NOT NULL,
            started_at  TEXT    NOT NULL,
            finished_at TEXT    NULL,
            succeeded   INTEGER NOT NULL DEFAULT 0,
            failed      INTEGER NOT NULL DEFAULT 0,
            summary     TEXT    NOT NULL DEFAULT ''
        );

        CREATE INDEX IF NOT EXISTS ix_operation_run_timeline
            ON operation_run (profile_key, started_at DESC);

        CREATE TABLE IF NOT EXISTS operation_item_outcome (
            id        INTEGER PRIMARY KEY AUTOINCREMENT,
            run_id    INTEGER NOT NULL REFERENCES operation_run (id),
            sequence  INTEGER NOT NULL,
            operation TEXT    NOT NULL,
            level     TEXT    NOT NULL,
            code      TEXT    NULL,
            title     TEXT    NOT NULL,
            board_id  INTEGER NULL,
            succeeded INTEGER NOT NULL,
            message   TEXT    NOT NULL DEFAULT ''
        );

        CREATE INDEX IF NOT EXISTS ix_operation_item_outcome_run
            ON operation_item_outcome (run_id, sequence);

        CREATE TABLE IF NOT EXISTS agent_run (
            id               INTEGER PRIMARY KEY AUTOINCREMENT,
            profile_key      TEXT    NOT NULL,
            provider_id      TEXT    NOT NULL,
            provider_version TEXT    NOT NULL,
            prompt           TEXT    NOT NULL,
            scope            TEXT    NOT NULL,
            scope_label      TEXT    NULL,
            started_at       TEXT    NOT NULL,
            finished_at      TEXT    NULL,
            status           TEXT    NOT NULL,
            exit_code        INTEGER NOT NULL DEFAULT 0,
            edit_accepted    INTEGER NULL,
            summary          TEXT    NOT NULL DEFAULT ''
        );

        CREATE INDEX IF NOT EXISTS ix_agent_run_timeline
            ON agent_run (profile_key, started_at DESC);
        """;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _connectionString;
    private SqliteConnection? _connection;
    private bool _disposed;

    internal HistoryDatabase(string databasePath)
    {
        DatabasePath = databasePath;

        // Pooling off on purpose: with it on, closing the connection returns the
        // file handle to a pool rather than to the operating system, and a caller
        // that wanted the file released still cannot have it.
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString();
    }

    internal string DatabasePath { get; }

    /// <summary>
    /// Runs one unit of work against the opened database. Every SQLite failure is
    /// converted here, so no caller of this store ever sees an exception cross the
    /// module boundary.
    /// </summary>
    internal async Task<Result<T>> RunAsync<T>(
        Func<SqliteConnection, CancellationToken, Task<Result<T>>> work,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var opened = await OpenAsync(cancellationToken).ConfigureAwait(false);
            if (opened.IsFailure)
            {
                return opened.Error!;
            }

            return await work(opened.Value, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            return Unavailable(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Unavailable(ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _connection?.Dispose();
        _connection = null;
        _gate.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
            _connection = null;
        }

        _gate.Dispose();
    }

    private Error Unavailable(string detail) => Error.SourceFailure(
        "history.unavailable", $"The operation history at {DatabasePath} could not be used: {detail}");

    private async Task<Result<SqliteConnection>> OpenAsync(CancellationToken cancellationToken)
    {
        if (_connection is not null)
        {
            return _connection;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(DatabasePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, Pragmas, cancellationToken).ConfigureAwait(false);

            var version = await ReadUserVersionAsync(connection, cancellationToken).ConfigureAwait(false);
            if (version > SchemaVersion)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                return Error.Conflict(
                    "history.schema_newer",
                    $"{DatabasePath} was written by a newer version of this app (schema {version}, this build knows {SchemaVersion}). Upgrade rather than letting an older build append rows a newer one has to interpret.");
            }

            await ExecuteAsync(connection, Schema, cancellationToken).ConfigureAwait(false);

            if (version < SchemaVersion)
            {
                // Pragmas take no parameters, and SchemaVersion is a compile-time
                // constant, so there is nothing here for a caller to inject.
                await ExecuteAsync(
                    connection, $"PRAGMA user_version = {SchemaVersion};", cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        _connection = connection;
        return connection;
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long> ReadUserVersionAsync(
        SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? 0L : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// How an instant is written to and read from the history.
///
/// Fixed-width ISO-8601 in UTC, so SQLite's lexicographic ordering of the text is
/// chronological ordering of the instants. Storing local time would make a machine
/// that moved timezone — or that crossed a daylight-saving boundary — read its own
/// history back out of order, which is the one thing an evidence log cannot do.
/// </summary>
internal static class HistoryTimestamp
{
    private const string Format = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    internal static string ToText(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture);

    internal static DateTimeOffset FromText(string text) => DateTimeOffset.ParseExact(
        text,
        Format,
        CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
