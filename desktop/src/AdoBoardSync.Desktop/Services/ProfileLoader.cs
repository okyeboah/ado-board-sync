using AdoBoardSync.Core.Backlog;
using AdoBoardSync.Core.Configuration;
using AdoBoardSync.Core.Csv;
using AdoBoardSync.Core.Markdown;
using AdoBoardSync.Core.Results;

namespace AdoBoardSync.Desktop.Services;

/// <summary>
///     Opens, saves and exports a Board profile through <see cref="IBacklogFileStore" />
///     (ABSD-107). Every method is asynchronous and cancellable, and the file work
///     runs off the calling thread — on the UI thread it is the render thread, and a
///     500-item backlog read there freezes the window (FSD NFR-2).
///     Core is deliberately synchronous: parsing is pure CPU work with no I/O to
///     await. The asynchrony is this caller's, which is why it lives here and not in
///     <c>BacklogParser</c>.
/// </summary>
public sealed class ProfileLoader(IBacklogFileStore store)
{
    private readonly IBacklogFileStore _store = store;

    /// <summary>Opens a profile from a <c>board.config.json</c> on disk.</summary>
    public async Task<Result<BacklogWorkspace>> LoadAsync(
        string configPath, CancellationToken cancellationToken = default)
    {
        var read = await Task.Run(
            () => _store.Read(configPath, IBacklogFileStore.ConfigScope), cancellationToken)
            .ConfigureAwait(false);

        if (read.IsFailure)
        {
            return read.Error!;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var directory = Path.GetDirectoryName(Path.GetFullPath(configPath))
            ?? Directory.GetCurrentDirectory();

        var parsed = BoardConfig.Parse(read.Value.Text, directory);
        return parsed.IsFailure
            ? parsed.Error!
            : await FromConfigAsync(parsed.Value, configPath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Opens a profile from a config already in memory, as onboarding builds it.</summary>
    public async Task<Result<BacklogWorkspace>> FromConfigAsync(
        BoardConfig config, string? configPath, CancellationToken cancellationToken = default)
    {
        var read = await Task.Run(() => _store.Read(config.BoardFile), cancellationToken)
            .ConfigureAwait(false);

        if (read.IsFailure)
        {
            // The backlog path comes from the config, so a reader who sees this
            // needs to know which document to correct.
            var error = read.Error!;
            var where = configPath is null ? "the profile" : configPath;
            return error.Code == "backlog.not_found"
                ? Error.NotFound(
                    error.Code,
                    $"Backlog file not found: {config.BoardFile}. Check \"board_file\" in {where}.")
                : error;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var stored = read.Value;
        var items = BacklogParser.Parse(config, stored.Text);
        return new BacklogWorkspace(
            configPath, config, config.BoardFile, stored.Text, items,
            BacklogMarkupAudit.Total(items), stored.Stamp);
    }

    /// <summary>
    ///     Re-reads the profile a workspace was opened from, picking up external
    ///     edits. An unsaved profile has no config file, so its config is reused.
    /// </summary>
    public Task<Result<BacklogWorkspace>> ReloadAsync(
        BacklogWorkspace workspace, CancellationToken cancellationToken = default) =>
        workspace.ConfigPath is { } path
            ? LoadAsync(path, cancellationToken)
            : FromConfigAsync(workspace.Config, null, cancellationToken);

    /// <summary>
    ///     Writes <paramref name="markdown" /> over the backlog file through the
    ///     store's atomic write and returns the re-parsed workspace it describes. If
    ///     the file changed on disk since this workspace was read, the save is
    ///     refused rather than silently overwriting the external edit (FSD §3.2.6) —
    ///     neither the buffer nor the file is discarded.
    /// </summary>
    public async Task<Result<BacklogWorkspace>> SaveAsync(
        BacklogWorkspace workspace, string markdown, CancellationToken cancellationToken = default)
    {
        var written = await Task.Run(
            () => WriteIfUnchanged(workspace, markdown), cancellationToken).ConfigureAwait(false);

        if (written.IsFailure)
        {
            return written.Error!;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var stored = written.Value;

        // Re-parsed from what was written, never from the buffer: PRD principle 1
        // forbids a shadow copy that can drift from the file the next parse reads.
        var items = BacklogParser.Parse(workspace.Config, stored.Text);
        return workspace with
        {
            Markdown = stored.Text,
            Items = items,
            MarkupProblemCount = BacklogMarkupAudit.Total(items),
            Stamp = stored.Stamp,
        };
    }

    /// <summary>
    ///     The save rule itself, free of the threading that carries it: stamp what is
    ///     on disk, refuse if the bytes moved since the profile was opened, otherwise
    ///     write atomically.
    /// </summary>
    private Result<StoredFile> WriteIfUnchanged(BacklogWorkspace workspace, string markdown)
    {
        // A file that is gone reads as `backlog.not_found` from Stamp, which is the
        // right answer — no separate Exists probe, which would be a second stat and
        // a race between the two.
        var current = _store.Stamp(workspace.BacklogPath);

        if (current.IsSuccess)
        {
            // Compared by content hash, not timestamp: a tool that rewrites identical
            // bytes moves the clock without changing the file, and refusing that save
            // would teach the user to ignore the warning that matters.
            if (current.Value.ContentDiffersFrom(workspace.Stamp))
            {
                return Error.Conflict(
                    "backlog.changed_on_disk",
                    $"{workspace.BacklogPath} changed outside the app after it was opened. "
                    + "Reload to pick up the external edits — your unsaved changes are still in the editor.");
            }
        }
        else if (current.Error!.Code != $"{IBacklogFileStore.BacklogScope}.not_found")
        {
            return current.Error!;
        }

        // A backlog that is not there yet is written, not refused: onboarding's
        // scaffold and a first save both land here.
        return _store.WriteAtomic(workspace.BacklogPath, markdown);
    }

    /// <summary>
    ///     Writes the import CSV from the parsed backlog — the same bytes the CLI's
    ///     <c>gen-csv</c> writes. An artefact for review and for the Azure DevOps web
    ///     importer: no credential, no network call, and never a source for Plans.
    /// </summary>
    public async Task<Result<CsvExport>> ExportCsvAsync(
        BacklogWorkspace workspace, string destinationPath, CancellationToken cancellationToken = default)
    {
        var text = ImportCsv.Serialize(workspace.Config, workspace.Items);
        var written = await Task.Run(
            () => _store.WriteAtomic(destinationPath, text, "csv"), cancellationToken)
            .ConfigureAwait(false);

        if (written.IsFailure)
        {
            return written.Error!;
        }

        // ImportCsv emits exactly one record per parsed item, so the count is the
        // item count — re-scanning the serialized text to rediscover it would be a
        // second pass that can only disagree with the writer.
        return new CsvExport(destinationPath, workspace.Items.Count, workspace.MarkupProblemCount);
    }

    /// <summary>True when a file is already at this path — the overwrite prompt reads it.</summary>
    public bool Exists(string path) => _store.Exists(path);

    /// <summary>
    /// The stamp of a file as it is on disk now, off the calling thread (ABSD-504).
    ///
    /// It runs on the pool for the same reason every other read here does: the
    /// staleness poll happens while the user is working, and a backlog on a slow
    /// network share would otherwise stall the render thread on a timer tick.
    /// </summary>
    public Task<Result<FileStamp>> StampAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() => _store.Stamp(path), cancellationToken);
}

/// <summary>What one CSV export wrote, for the report the window shows afterwards.</summary>
public sealed record CsvExport(string Path, int RowCount, int MarkupProblemCount);
