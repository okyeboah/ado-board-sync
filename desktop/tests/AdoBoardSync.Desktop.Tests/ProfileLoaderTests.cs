using System.Text.Json.Nodes;
using AdoBoardSync.Core.Backlog;
using AdoBoardSync.Core.Results;
using AdoBoardSync.Desktop.Services;
using AdoBoardSync.TestKit;

namespace AdoBoardSync.Desktop.Tests;

/// <summary>
///     ABSD-107 stated as tests: opening, saving and exporting a profile go through
///     <see cref="IBacklogFileStore" />, asynchronously and cancellably.
///     Every test here drives the whole path with a fake store, so the assertions can
///     be about the seam itself — which paths were read, which were written, and on
///     which thread — rather than about a temporary directory. Nothing in this file
///     creates a file, and the store's read and write logs are the evidence that
///     nothing tried to.
/// </summary>
public sealed class ProfileLoaderTests
{
    private const string ConfigPath = "/fixture/board.config.json";

    private const string BacklogPath = InMemoryConfig.DefaultBacklogPath;

    private const string Backlog =
        "## Epic 1 - Foundations\n"
        + "\n"
        + "### PROJ-101 - Build the store\n"
        + "- append events\n"
        + "\n"
        + "### PROJ-102 - Wire the shell\n"
        + "- bind the tree\n";

    [Fact]
    public async Task ALoadThroughAFakeStoreParsesTheBacklogAndReadsBothFilesThroughTheSeam()
    {
        var store = Profile();

        var loaded = await new ProfileLoader(store).LoadAsync(ConfigPath);

        Assert.True(loaded.IsSuccess, loaded.Error?.SafeMessage);
        var workspace = loaded.Value;
        Assert.Equal(
            ["Epic 1 - Foundations", "PROJ-101 - Build the store", "PROJ-102 - Wire the shell"],
            workspace.Items.Select(item => item.Title));
        Assert.Equal([null, "PROJ-101", "PROJ-102"], workspace.Items.Select(item => item.Code));
        Assert.Equal(["append events"], workspace.Items[1].Bullets);
        Assert.Equal(["bind the tree"], workspace.Items[2].Bullets);
        Assert.Equal(BacklogPath, workspace.BacklogPath);
        Assert.Equal([ConfigPath, BacklogPath], store.Reads);
    }

    [Fact]
    public async Task AMissingBacklogIsReportedAsNotFoundNamingThePathTheConfigGave()
    {
        var store = new InMemoryBacklogFileStore();
        var config = InMemoryConfig.Create();

        var loaded = await new ProfileLoader(store).FromConfigAsync(config, ConfigPath);

        Assert.True(loaded.IsFailure);
        Assert.Equal("backlog.not_found", loaded.Error!.Code);
        Assert.Equal(ErrorKind.NotFound, loaded.Error.Kind);
        Assert.Contains(BacklogPath, loaded.Error.SafeMessage, StringComparison.Ordinal);
        Assert.Contains(ConfigPath, loaded.Error.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStoreIsReadOffTheThreadThatCalledLoad()
    {
        var store = Profile();
        var readingThreadIds = new List<int>();
        store.BeforeRead = _ => readingThreadIds.Add(Environment.CurrentManagedThreadId);

        var (callerThreadId, loaded) = LoadOnACallerThreadOfItsOwn(store);

        Assert.True(loaded.IsSuccess, loaded.Error?.SafeMessage);
        Assert.Equal(2, readingThreadIds.Count);
        Assert.All(readingThreadIds, id => Assert.NotEqual(callerThreadId, id));
    }

    [Fact]
    public async Task AnAlreadyCancelledLoadThrowsRatherThanReturningAFailureResult()
    {
        var store = Profile();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        // The observed shape, pinned here because a caller has to pick one: Task.Run
        // refuses a pre-cancelled token before it queues the delegate, so the caller
        // gets OperationCanceledException and never a Result.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new ProfileLoader(store).LoadAsync(ConfigPath, cancelled.Token));

        Assert.Empty(store.Reads);
    }

    [Fact]
    public async Task CancellingWhileTheBacklogIsBeingReadLeavesTheCallerWithNoWorkspaceAtAll()
    {
        var store = Profile();
        using var cancelling = new CancellationTokenSource();
        store.BeforeRead = path =>
        {
            if (string.Equals(path, BacklogPath, StringComparison.Ordinal))
            {
                cancelling.Cancel();
            }
        };

        Result<BacklogWorkspace>? outcome = null;
        var thrown = await Record.ExceptionAsync(async () =>
        {
            outcome = await new ProfileLoader(store).LoadAsync(ConfigPath, cancelling.Token);
        });

        Assert.NotNull(thrown);
        Assert.IsAssignableFrom<OperationCanceledException>(thrown);

        // The stronger half: the load got as far as the backlog read, so a partially
        // built profile existed inside the loader — and still no Result escaped, which
        // is what stops a half-populated workspace reaching a window.
        Assert.Equal([ConfigPath, BacklogPath], store.Reads);
        Assert.Null(outcome);
    }

    [Fact]
    public async Task SaveReturnsAWorkspaceReparsedFromWhatWasWrittenRatherThanFromTheBuffer()
    {
        var store = Profile();
        var loader = new ProfileLoader(store);
        var opened = await loader.LoadAsync(ConfigPath);
        Assert.True(opened.IsSuccess, opened.Error?.SafeMessage);

        const string rewritten =
            "## Epic 1 - Foundations\n"
            + "\n"
            + "### PROJ-301 - The only issue left\n"
            + "- a single task\n";

        var saved = await loader.SaveAsync(opened.Value, rewritten);

        Assert.True(saved.IsSuccess, saved.Error?.SafeMessage);
        Assert.Equal([BacklogPath], store.Writes);
        Assert.Equal(rewritten, store.TextAt(BacklogPath));
        Assert.Equal(rewritten, saved.Value.Markdown);
        Assert.Equal(
            ["PROJ-301"],
            saved.Value.Items.Where(item => item.Level == BacklogLevel.Issue).Select(item => item.Code));
        Assert.Equal(["a single task"], saved.Value.Items[1].Bullets);
    }

    [Fact]
    public async Task ASaveIsRefusedWhenTheBacklogChangedOnDiskAndNothingIsWritten()
    {
        var store = Profile();
        var loader = new ProfileLoader(store);
        var opened = await loader.LoadAsync(ConfigPath);
        Assert.True(opened.IsSuccess, opened.Error?.SafeMessage);

        const string external =
            "## Epic 1 - Foundations\n"
            + "\n"
            + "### PROJ-901 - Written by another editor\n";
        store.ChangeExternally(BacklogPath, external);

        var saved = await loader.SaveAsync(opened.Value, "## Epic 1 - Foundations\n");

        Assert.True(saved.IsFailure);
        Assert.Equal("backlog.changed_on_disk", saved.Error!.Code);
        Assert.Equal(ErrorKind.Conflict, saved.Error.Kind);
        Assert.Empty(store.Writes);
        Assert.Equal(external, store.TextAt(BacklogPath));
    }

    [Fact]
    public async Task AnExternalRewriteWithIdenticalContentDoesNotRefuseTheSave()
    {
        var store = Profile();
        var loader = new ProfileLoader(store);
        var opened = await loader.LoadAsync(ConfigPath);
        Assert.True(opened.IsSuccess, opened.Error?.SafeMessage);

        // Same bytes, later timestamp — the store bumps its clock on every write. The
        // guard reads content hashes, so this must not cost the user their save.
        store.ChangeExternally(BacklogPath, Backlog);

        var saved = await loader.SaveAsync(opened.Value, Backlog + "\n### PROJ-103 - Added later\n");

        Assert.True(saved.IsSuccess, saved.Error?.SafeMessage);
        Assert.Equal([BacklogPath], store.Writes);
        Assert.Equal(
            ["PROJ-101", "PROJ-102", "PROJ-103"],
            saved.Value.Items.Where(item => item.Level == BacklogLevel.Issue).Select(item => item.Code));
    }

    [Fact]
    public async Task AnExportWritesTheCsvThroughTheStoreWithNoBoardInvolved()
    {
        // Heading-only on purpose: ExportCsvAsync counts physical lines, so a
        // description that renders as an HTML list puts newlines inside a quoted CSV
        // field and the reported count then exceeds the number of work items. With no
        // descriptions, one item is one line and the count means what it says.
        var store = Profile(
            "## Epic 1 - Foundations\n"
            + "\n"
            + "### PROJ-101 - Build the store\n"
            + "\n"
            + "### PROJ-102 - Wire the shell\n");
        var loader = new ProfileLoader(store);
        var opened = await loader.LoadAsync(ConfigPath);
        Assert.True(opened.IsSuccess, opened.Error?.SafeMessage);
        const string destination = "/fixture/export/work-items.csv";

        var export = await loader.ExportCsvAsync(opened.Value, destination);

        Assert.True(export.IsSuccess, export.Error?.SafeMessage);
        Assert.Equal(destination, export.Value.Path);
        Assert.Equal(opened.Value.Items.Count, export.Value.RowCount);
        Assert.Equal(opened.Value.MarkupProblemCount, export.Value.MarkupProblemCount);
        Assert.Equal([destination], store.Writes);

        // Offline by construction: the export takes its destination from the caller,
        // so the config's csv_file is not consulted and there is no board to reach.
        Assert.DoesNotContain(opened.Value.Config.CsvFile, store.Writes);
        Assert.Null(store.TextAt(opened.Value.Config.CsvFile));
    }

    /// <summary>
    ///     Runs one load on a thread that exists only for it and waits there until it
    ///     finishes, reporting that thread's id alongside the result.
    ///     A test thread will not do: xunit runs it on the thread pool, and the moment
    ///     the load reaches its first await that thread goes back to the pool, which may
    ///     then pick it to run the very next read. That is correct behaviour and would
    ///     still fail an "off the calling thread" assertion, so the caller here is a
    ///     thread of its own that the pool can never reach into.
    /// </summary>
    private static (int CallerThreadId, Result<BacklogWorkspace> Loaded) LoadOnACallerThreadOfItsOwn(
        InMemoryBacklogFileStore store)
    {
        var callerThreadId = 0;
        var loaded = default(Result<BacklogWorkspace>);

        var caller = new Thread(() =>
        {
            callerThreadId = Environment.CurrentManagedThreadId;
            loaded = new ProfileLoader(store).LoadAsync(ConfigPath).GetAwaiter().GetResult();
        });

        caller.Start();
        caller.Join();
        return (callerThreadId, loaded);
    }

    /// <summary>
    ///     A store holding one whole profile. The config is seeded as JSON text rather
    ///     than as a parsed object because <c>LoadAsync</c> is the path under test: it
    ///     reads its config back out of the store, so the store has to hold a document.
    /// </summary>
    private static InMemoryBacklogFileStore Profile(string backlog = Backlog) =>
        new InMemoryBacklogFileStore()
            .With(ConfigPath, ConfigJson())
            .With(BacklogPath, backlog);

    private static string ConfigJson() =>
        new JsonObject
        {
            ["org"] = "demo-org",
            ["project"] = "DemoProject",
            ["code_prefix"] = "PROJ",
            ["board_file"] = BacklogPath,
        }.ToJsonString();
}
