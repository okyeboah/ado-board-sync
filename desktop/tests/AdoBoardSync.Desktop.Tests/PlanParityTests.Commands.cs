using System.Diagnostics;
using AdoBoardSync.Core.Backlog;
using AdoBoardSync.Core.Board;
using AdoBoardSync.Core.Configuration;
using AdoBoardSync.Core.Markdown;
using AdoBoardSync.Core.Planning;
using AdoBoardSync.Infrastructure;
using AdoBoardSync.TestKit;

namespace AdoBoardSync.Desktop.Tests;

/// <summary>
///     Apply-parity for the commands the first table grew and for <c>import</c>,
///     whose scenarios live in a partial class because the shared harness in
///     <see cref="PlanParityTests" /> had no line budget left. Same contract: given
///     the same backlog and the same starting board, both implementations leave the
///     board in exactly the same state.
/// </summary>
public sealed partial class PlanParityTests
{
    /// <summary>The profile config with the start/working states <c>advance</c> plans against.</summary>
    private const string ConfigWithStatesJson = """
                                                {
                                                  "org": "contoso",
                                                  "project": "widgets",
                                                  "code_prefix": "PROJ",
                                                  "board_file": "backlog.md",
                                                  "csv_file": "work-items.csv",
                                                  "states": { "todo": "New", "doing": "Active" }
                                                }
                                                """;

    /// <summary>
    ///     The description each item would have on a board that matches the backlog,
    ///     taken from the parsed backlog rather than written out here.
    ///     Hand-written HTML was wrong in a way that took a failing parity test to
    ///     notice: an Issue's description includes its own bullet list, because the
    ///     parser leaves the bullets in <c>desc_lines</c> as well as lifting them into
    ///     Tasks. A fixture that states the answer independently states it wrong.
    /// </summary>
    private (string Epic, string First, string Second) MatchingDescriptions()
    {
        var (_, items, _) = Profile();

        string For(Func<BacklogItem, bool> match)
        {
            return MarkdownHtml.ToHtml(items.First(match).DescriptionLines);
        }

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

    [Fact]
    public async Task ImportLeavesTheSameBoardTheCliLeaves()
    {
        // PROJ-102 and everything under it is missing; import creates the Issue
        // (and only the Issue — Tasks are resync-tasks' business) under the Epic.
        var seed = MatchingBoard().Where(i => i.Id != 3).ToList();

        var (cli, port) = await RunBothAsync(
            "import",
            (config, items, snapshot, markdown) => PlanBuilder.BuildImport(config, items, snapshot, markdown),
            seed);

        AssertSameBoard(cli, port);
    }

    [Fact]
    public async Task SyncLeavesTheSameBoardTheCliLeaves()
    {
        var (epic, first, second) = MatchingDescriptions();
        var seed = new List<BoardWorkItem>
        {
            new() { Id = 1, WorkItemType = "Epic", Title = "Epic 1: Foundation", Description = epic, State = "Done" },
            new()
            {
                Id = 2, WorkItemType = "Issue", Title = "PROJ-101 · Renamed on the board", Description = "<p>stale</p>",
                ParentId = 1, State = "Active"
            },
            new() { Id = 4, WorkItemType = "Task", Title = "A stray task", ParentId = 2, State = "New" }
        };

        var (cli, port) = await RunBothAsync(
            "sync",
            (config, items, snapshot, markdown) => PlanBuilder.BuildSync(config, items, snapshot, markdown).Value,
            seed);

        AssertSameBoard(cli, port);
    }

    [Fact]
    public async Task SetStateLeavesTheSameBoardTheCliLeaves()
    {
        // The Epic rides along because the seed names item 2's parent.
        var seed = MatchingBoard().Where(i => i.Id is 1 or 2).ToList();

        var (cli, port) = await RunBothAsync(
            "set-state",
            (config, items, snapshot, markdown) =>
                PlanBuilder.BuildSetState(config, snapshot, markdown, [2]),
            seed,
            "--ids", "2");

        AssertSameBoard(cli, port);
    }

    [Fact]
    public async Task AdvanceLeavesTheSameBoardTheCliLeaves()
    {
        var repo = CreateEvidenceRepo();

        // MatchingBoard rewrites board.config.json through Profile(), so it runs
        // first and the states config is what the driver actually reads.
        var seed = MatchingBoard()
            .Where(i => i.WorkItemType != "Task")
            .Select(i => i with { State = i.Title.StartsWith("PROJ-101") ? "New" : "Active" })
            .ToList();
        var (config, items, markdown) = ProfileWith(ConfigWithStatesJson);

        var reference = PythonReference.Apply(
            Serialize(seed), "--config", ConfigPath, "--command", "advance", "--repo", repo, "--base", "main",
            "--no-fetch");

        // Both sides probe the same physical repository, so the evidence — and
        // therefore the writes — describe one real git state, not two fakes.
        var probed = await Probe(repo, config);
        var board = new FakeBoardGateway();
        board.Items.AddRange(seed);
        var snapshot = (await board.ReadAsync(config)).Value;
        var plan = PlanBuilder
            .BuildAdvance(config, items, snapshot, markdown, probed, "main")
            .Value;

        var applied = await ApplyExecutor.ApplyAsync(
            board, config, plan, PlanBuilder.FingerprintBacklog(markdown), snapshot.Fingerprint);

        Assert.True(applied.IsSuccess, applied.Error?.SafeMessage);
        Assert.True(applied.Value.AllSucceeded, string.Join("; ",
            applied.Value.Outcomes.Where(o => !o.Succeeded).Select(o => o.Message)));

        Assert.True(reference.RootElement.GetProperty("exitCode").GetInt32() == 0,
            "CLI advance exited non-zero. Its report: "
            + reference.RootElement.GetProperty("report").GetString());
        AssertSameBoard(
            Describe(reference.RootElement.GetProperty("board")),
            Describe(board));
    }


    /// <summary>
    ///     A local repository whose <c>feature/PROJ-101</c> branch holds one commit
    ///     beyond <c>main</c> — the evidence <c>advance</c> exists to notice.
    /// </summary>
    private string CreateEvidenceRepo()
    {
        var repo = Path.Combine(_directory, "evidence-repo");
        Directory.CreateDirectory(repo);
        Git(repo, "init", "-b", "main");
        Git(repo, "config", "user.email", "parity@example.com");
        Git(repo, "config", "user.name", "Parity");
        File.WriteAllText(Path.Combine(repo, "readme.md"), "base\n");
        Git(repo, "add", ".");
        Git(repo, "commit", "-m", "base");
        Git(repo, "checkout", "-b", "feature/PROJ-101");
        File.WriteAllText(Path.Combine(repo, "readme.md"), "work\n");
        Git(repo, "add", ".");
        Git(repo, "commit", "-m", "PROJ-101 work");
        return repo;
    }

    private static void Git(string repo, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-C");
        start.ArgumentList.Add(repo);
        foreach (var arg in args) start.ArgumentList.Add(arg);

        using var process = Process.Start(start)
                            ?? throw new InvalidOperationException("git could not start");
        var errors = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {errors}");
    }

    private static async Task<GitProbeReport> Probe(string repo, BoardConfig config)
    {
        var probed = await new GitEvidenceAdapter()
            .ProbeAsync([repo], "main", false, config.IssueCodeRegex.ToString());

        Assert.True(probed.IsSuccess, probed.Error?.SafeMessage);
        return probed.Value;
    }
}