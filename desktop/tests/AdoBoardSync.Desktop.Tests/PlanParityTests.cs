using System.Text.Json;
using AdoBoardSync.Core.Backlog;
using AdoBoardSync.Core.Board;
using AdoBoardSync.Core.Configuration;
using AdoBoardSync.Core.Markdown;
using AdoBoardSync.Core.Planning;
using AdoBoardSync.TestKit;

namespace AdoBoardSync.Desktop.Tests;

/// <summary>
/// The plan-computation parity gate CONVENTIONS.md rule 5 asks for.
///
/// Every other parity suite compares a pure function's output. A Plan cannot be
/// compared that way: the two implementations word their dry-runs differently and
/// always will. So this compares the thing that actually matters — given the same
/// backlog and the same starting board, both implementations must leave the board
/// in exactly the same state. The CLI runs its real command against its own
/// FakeClient through <c>parity_driver.py apply</c>; the port builds a Plan and
/// applies it through <see cref="FakeBoardGateway" />; the two boards are compared
/// field for field.
///
/// It lives in this project rather than AdoBoardSync.Parity.Tests because the fake
/// gateway and the Apply Executor are here, and moving either would be a bigger
/// change than the convention is worth.
/// </summary>
public sealed class PlanParityTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateTempSubdirectory("ado-board-sync-plan-parity").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leaked temp directory must not fail a passing test.
        }
    }

    private const string Backlog = """
        ## Epic 1: Foundation

        Epic body.

        ### PROJ-101 · First issue

        First body.

        - Do the first thing
        - Do the second thing

        ### PROJ-102 · Second issue

        Second body.
        """;

    private const string ConfigJson = """
        {
          "org": "contoso",
          "project": "widgets",
          "code_prefix": "PROJ",
          "board_file": "backlog.md",
          "assignees": { "ada@example.com": ["PROJ-101"], "grace@example.com": ["PROJ-102"] },
          "iterations": [
            { "name": "Sprint 1", "items": ["PROJ-101"] },
            { "name": "Sprint 2", "items": ["PROJ-102"] }
          ]
        }
        """;

    /// <summary>
    /// A clean board: one Epic, both Issues, both Tasks, nothing duplicated.
    /// Deliberately clean — see <see cref="TheTwoImplementationsDisagreeOnWhichDuplicateAssignPicks" />
    /// for the one state where the two implementations differ, and why the fixtures
    /// avoid it.
    /// </summary>
    private static IReadOnlyList<BoardWorkItem> CleanBoard(string epicHtml, string firstHtml, string secondHtml) =>
    [
        new() { Id = 1, WorkItemType = "Epic", Title = "Epic 1: Foundation", Description = epicHtml, State = "Done" },
        new() { Id = 2, WorkItemType = "Issue", Title = "PROJ-101 · First issue", Description = firstHtml, ParentId = 1, State = "Active" },
        new() { Id = 3, WorkItemType = "Issue", Title = "PROJ-102 · Second issue", Description = secondHtml, ParentId = 1, State = "New" },
        new() { Id = 4, WorkItemType = "Task", Title = "Do the first thing", ParentId = 2, State = "New" },
        new() { Id = 5, WorkItemType = "Task", Title = "Do the second thing", ParentId = 2, State = "Done" },
    ];

    private (BoardConfig Config, IReadOnlyList<BacklogItem> Items, string Markdown) Profile()
    {
        var configPath = Path.Combine(_directory, "board.config.json");
        File.WriteAllText(configPath, ConfigJson);
        File.WriteAllText(Path.Combine(_directory, "backlog.md"), Backlog);

        var config = BoardConfig.Load(configPath).Value;
        return (config, BacklogParser.Parse(config, Backlog), Backlog);
    }

    private string ConfigPath => Path.Combine(_directory, "board.config.json");

    /// <summary>The board as the driver takes it on stdin.</summary>
    private static string Serialize(IEnumerable<BoardWorkItem> board) =>
        JsonSerializer.Serialize(new
        {
            board = board.Select(i => new
            {
                id = i.Id,
                type = i.WorkItemType,
                title = i.Title,
                description = i.Description,
                parentId = i.ParentId,
                state = i.State,
                assignedTo = i.AssignedTo,
                iterationPath = i.IterationPath,
            }),
        });

    /// <summary>One item, flattened to the fields both sides record.</summary>
    private static string Describe(int id, string type, string title, string description,
        int? parentId, string state, string assignedTo, string iterationPath) =>
        $"#{id} {type} | {title} | {description} | parent={parentId?.ToString() ?? "-"} "
        + $"| state={state} | assigned={assignedTo} | iteration={iterationPath}";

    private static IReadOnlyList<string> Describe(JsonElement board) =>
        [.. board.EnumerateArray().Select(i => Describe(
            i.GetProperty("id").GetInt32(),
            i.GetProperty("type").GetString() ?? string.Empty,
            i.GetProperty("title").GetString() ?? string.Empty,
            i.GetProperty("description").GetString() ?? string.Empty,
            i.GetProperty("parentId").ValueKind == JsonValueKind.Null
                ? null
                : i.GetProperty("parentId").GetInt32(),
            i.GetProperty("state").GetString() ?? string.Empty,
            i.GetProperty("assignedTo").GetString() ?? string.Empty,
            i.GetProperty("iterationPath").GetString() ?? string.Empty))];

    private static IReadOnlyList<string> Describe(FakeBoardGateway board) =>
        [.. board.Items.OrderBy(i => i.Id).Select(i => Describe(
            i.Id, i.WorkItemType, i.Title, i.Description, i.ParentId,
            i.State, i.AssignedTo, i.IterationPath))];

    /// <summary>
    /// Runs the CLI command through the driver and the .NET Plan through the fake
    /// gateway, and returns both boards for comparison.
    /// </summary>
    private async Task<(IReadOnlyList<string> Cli, IReadOnlyList<string> Port)> RunBothAsync(
        string command,
        Func<BoardConfig, IReadOnlyList<BacklogItem>, BoardSnapshot, string, Plan> build,
        IReadOnlyList<BoardWorkItem> seed,
        params string[] extraFlags)
    {
        // The profile is written first: it is what puts board.config.json and the
        // backlog on disk, and the driver reads both.
        var (config, items, markdown) = Profile();

        var reference = PythonReference.Apply(
            Serialize(seed),
            ["--config", ConfigPath, "--command", command, .. extraFlags]);

        var board = new FakeBoardGateway();
        board.Items.AddRange(seed);

        var snapshot = (await board.ReadAsync(config)).Value;
        var plan = build(config, items, snapshot, markdown);

        var applied = await ApplyExecutor.ApplyAsync(
            board, config, plan, PlanBuilder.FingerprintBacklog(markdown), snapshot.Fingerprint);

        Assert.True(applied.IsSuccess, applied.Error?.SafeMessage);
        Assert.True(applied.Value.AllSucceeded, string.Join("; ",
            applied.Value.Outcomes.Where(o => !o.Succeeded).Select(o => o.Message)));

        return (Describe(reference.RootElement.GetProperty("board")), Describe(board));
    }

    private static string Html(params string[] lines) => MarkdownHtml.ToHtml(lines);

    /// <summary>
    /// The description each item would have on a board that matches the backlog,
    /// taken from the parsed backlog rather than written out here.
    ///
    /// Hand-written HTML was wrong in a way that took a failing parity test to
    /// notice: an Issue's description includes its own bullet list, because the
    /// parser leaves the bullets in <c>desc_lines</c> as well as lifting them into
    /// Tasks. A fixture that states the answer independently states it wrong.
    /// </summary>
    private (string Epic, string First, string Second) MatchingDescriptions()
    {
        var (_, items, _) = Profile();

        string For(Func<BacklogItem, bool> match) =>
            MarkdownHtml.ToHtml(items.First(match).DescriptionLines);

        return (
            For(i => i.Level == BacklogLevel.Epic),
            For(i => i.Code == "PROJ-101"),
            For(i => i.Code == "PROJ-102"));
    }

    /// <summary>A board that matches the backlog exactly.</summary>
    private IReadOnlyList<BoardWorkItem> MatchingBoard()
    {
        var (epic, first, second) = MatchingDescriptions();
        return CleanBoard(epic, first, second);
    }

    /// <summary>
    /// Compares the two boards item by item. xunit truncates a collection failure
    /// to the first fifty characters of each element, which for these rows is the
    /// part that always matches — so the first differing pair is asserted on its
    /// own, where the message shows both lines whole.
    /// </summary>
    private static void AssertSameBoard(IReadOnlyList<string> cli, IReadOnlyList<string> port)
    {
        foreach (var index in Enumerable.Range(0, Math.Min(cli.Count, port.Count)))
        {
            if (!string.Equals(cli[index], port[index], StringComparison.Ordinal))
            {
                Assert.Fail(
                    $"The two implementations left different boards at item {index}.\n"
                    + $"  CLI : {cli[index]}\n"
                    + $"  port: {port[index]}");
            }
        }

        Assert.Equal(cli.Count, port.Count);
    }

    [Fact]
    public async Task DedupLeavesTheSameBoardTheCliLeaves()
    {
        // Two extra copies: a duplicated Issue code, and a duplicated Task title
        // under one parent. Both are what dedup exists to remove.
        var seed = new List<BoardWorkItem>(MatchingBoard())
        {
            new() { Id = 6, WorkItemType = "Issue", Title = "PROJ-101 · First issue (copy)", ParentId = 1, State = "New" },
            new() { Id = 7, WorkItemType = "Task", Title = "Do the first thing", ParentId = 2, State = "New" },
        };

        var (cli, port) = await RunBothAsync(
            "dedup", (c, _, s, m) => PlanBuilder.BuildDedup(c, s, m), seed);

        AssertSameBoard(cli, port);
    }

    [Fact]
    public async Task CloseChildrenLeavesTheSameBoardTheCliLeaves()
    {
        var seed = MatchingBoard();

        var (cli, port) = await RunBothAsync(
            "close-children", (c, _, s, m) => PlanBuilder.BuildCloseChildren(c, s, m), seed);

        AssertSameBoard(cli, port);
    }

    [Fact]
    public async Task CloseChildrenWithAssignFromParentLeavesTheSameBoardTheCliLeaves()
    {
        var seed = MatchingBoard()
            .Select(i => i.Id == 1 ? i with { AssignedTo = "ada@example.com" } : i)
            .ToArray();

        var (cli, port) = await RunBothAsync(
            "close-children",
            (c, _, s, m) => PlanBuilder.BuildCloseChildren(c, s, m, assignFromParent: true),
            seed,
            "--assign-from-parent");

        AssertSameBoard(cli, port);
    }

    [Fact]
    public async Task AssignLeavesTheSameBoardTheCliLeaves()
    {
        var seed = MatchingBoard();

        var (cli, port) = await RunBothAsync(
            "assign", (c, _, s, m) => PlanBuilder.BuildAssign(c, s, m), seed);

        AssertSameBoard(cli, port);
    }

    [Fact]
    public async Task AssignOnlyUnassignedLeavesTheSameBoardTheCliLeaves()
    {
        var seed = MatchingBoard()
            .Select(i => i.Id == 2 ? i with { AssignedTo = "someone.else@example.com" } : i)
            .ToArray();

        var (cli, port) = await RunBothAsync(
            "assign",
            (c, _, s, m) => PlanBuilder.BuildAssign(c, s, m, onlyUnassigned: true),
            seed,
            "--only-unassigned");

        AssertSameBoard(cli, port);
    }

    [Fact]
    public async Task AssignWithoutTasksLeavesTheSameBoardTheCliLeaves()
    {
        var seed = MatchingBoard();

        var (cli, port) = await RunBothAsync(
            "assign",
            (c, _, s, m) => PlanBuilder.BuildAssign(c, s, m, includeTasks: false),
            seed,
            "--no-tasks");

        AssertSameBoard(cli, port);
    }

    [Fact]
    public async Task SprintsAssignOnlyLeavesTheSameBoardTheCliLeaves()
    {
        // --assign-only, because iteration nodes are classification nodes rather
        // than work items: they do not appear on either side's board dump, so
        // comparing boards would say nothing about them either way.
        var seed = MatchingBoard();

        var (cli, port) = await RunBothAsync(
            "sprints",
            (c, _, s, m) => PlanBuilder.BuildSprints(c, s, m, assignOnly: true),
            seed,
            "--assign-only");

        AssertSameBoard(cli, port);
    }

    [Fact]
    public async Task ResyncLeavesTheSameBoardTheCliLeaves()
    {
        // The board's descriptions and one title have drifted from the backlog.
        var seed = CleanBoard("<div>stale epic</div>", "<div>stale first</div>", Html("Second body."))
            .Select(i => i.Id == 2 ? i with { Title = "PROJ-101 · Stale title" } : i)
            .ToArray();

        var (cli, port) = await RunBothAsync(
            "resync", (c, i, s, m) => PlanBuilder.BuildResync(c, i, s, m), seed);

        AssertSameBoard(cli, port);
    }

    [Fact]
    public async Task ResyncTasksLeavesTheSameBoardTheCliLeaves()
    {
        var seed = new List<BoardWorkItem>(MatchingBoard())
        {
            // A Task whose bullet has left the backlog: resync-tasks deletes it.
            new() { Id = 8, WorkItemType = "Task", Title = "Do a thing nobody asked for", ParentId = 2, State = "New" },
        };

        var (cli, port) = await RunBothAsync(
            "resync-tasks", (c, i, s, m) => PlanBuilder.BuildResyncTasks(c, i, s, m), seed);

        AssertSameBoard(cli, port);
    }

    [Fact]
    public async Task AuditAgreesWithTheCliOnWhetherTheBoardIsClean()
    {
        // audit writes nothing, so the board comparison proves only that neither
        // side wrote. The claim worth making is the verdict: the CLI exits 1 on
        // drift, and a board it fails must be a board this reports as not clean.
        var (config, items, markdown) = Profile();
        var drifted = CleanBoard("<div>stale epic</div>", "<div>stale first</div>", Html("Second body."));

        var reference = PythonReference.Apply(
            Serialize(drifted), "--config", ConfigPath, "--command", "audit");

        var board = new FakeBoardGateway();
        board.Items.AddRange(drifted);
        var snapshot = (await board.ReadAsync(config)).Value;

        var report = PlanBuilder.BuildAudit(config, items, snapshot, markdown);

        Assert.Equal(1, reference.RootElement.GetProperty("exitCode").GetInt32());
        Assert.False(report.IsClean);
        Assert.Empty(board.Updated);
        Assert.Empty(board.Created);
        Assert.Empty(board.Deleted);
    }

    [Fact]
    public async Task AuditAgreesWithTheCliOnABoardThatMatches()
    {
        var (config, items, markdown) = Profile();

        // Every Task closed under a Done Epic, so no state drift either.
        var clean = MatchingBoard()
            .Select(i => i with { State = "Done" })
            .ToArray();

        var reference = PythonReference.Apply(
            Serialize(clean), "--config", ConfigPath, "--command", "audit");

        var board = new FakeBoardGateway();
        board.Items.AddRange(clean);
        var snapshot = (await board.ReadAsync(config)).Value;

        var report = PlanBuilder.BuildAudit(config, items, snapshot, markdown);

        Assert.Equal(0, reference.RootElement.GetProperty("exitCode").GetInt32());
        Assert.True(report.IsClean, string.Join("; ", report.Findings.Select(f => f.Detail)));
    }

    [Fact]
    public async Task TheTwoImplementationsDisagreeOnWhichDuplicateAssignPicks()
    {
        // A real, deliberate divergence, pinned so it cannot change unnoticed.
        //
        // The CLI builds its code->item map by overwriting as it walks ids in
        // ascending order, so a duplicated code leaves the HIGHEST id owning the
        // code and that is the item it assigns. This port takes the LOWEST, which
        // is what dedup keeps and what every other command here treats as the real
        // item. Neither is wrong on a board `audit` passes — a duplicated code is
        // drift audit already fails on, and dedup removes the copy this port
        // ignores. The parity fixtures above are all clean boards for that reason.
        var seed = new List<BoardWorkItem>(MatchingBoard())
        {
            new() { Id = 9, WorkItemType = "Issue", Title = "PROJ-101 · First issue (copy)", ParentId = 1, State = "New" },
        };

        var (cli, port) = await RunBothAsync(
            "assign",
            (c, _, s, m) => PlanBuilder.BuildAssign(c, s, m, includeTasks: false),
            seed,
            "--no-tasks");

        Assert.NotEqual(cli, port);
        Assert.Contains(cli, line => line.StartsWith("#9 ", StringComparison.Ordinal) && line.Contains("assigned=ada@example.com"));
        Assert.Contains(port, line => line.StartsWith("#2 ", StringComparison.Ordinal) && line.Contains("assigned=ada@example.com"));
    }
}
