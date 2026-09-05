using AdoBoardSync.Core.Configuration;

namespace AdoBoardSync.Core.Tests;

/// <summary>
/// Pins the config write-back the sprint and assignee views depend on
/// (ABSD-401/402, FSD §5). The rule this file exists to protect is that writing
/// one section must not rewrite any other: the config is a shared, hand-edited,
/// version-controlled file, and a save that reformats it or bakes in this
/// machine's defaults would make the app unusable on a team.
/// </summary>
public class BoardConfigWriterTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateTempSubdirectory("ado-board-sync-config").FullName;

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

        GC.SuppressFinalize(this);
    }

    private string WriteConfig(string json)
    {
        var path = Path.Combine(_directory, "board.config.json");
        File.WriteAllText(path, json);
        return path;
    }

    private const string Minimal = """
        {
          "org": "contoso",
          "project": "widgets",
          "code_prefix": "PROJ",
          "board_file": "docs/backlog.md",
          "task_title_max": 120
        }
        """;

    [Fact]
    public void WritingIterationsLeavesEveryOtherKeyExactlyAsItWas()
    {
        var path = WriteConfig(Minimal);

        var written = BoardConfigWriter.WriteIterations(
            path, [new IterationConfig("Sprint 1", "2026-01-05", "2026-01-16", ["PROJ-101"])]);

        Assert.True(written.IsSuccess);

        var reloaded = BoardConfig.Load(path).Value;
        Assert.Equal("contoso", reloaded.Org);
        Assert.Equal("PROJ", reloaded.CodePrefix);
        Assert.Equal(120, reloaded.TaskTitleMax);
        Assert.Equal("Sprint 1", Assert.Single(reloaded.Iterations).Name);
    }

    [Fact]
    public void ARelativeBoardFileStaysRelative()
    {
        // BoardConfig resolves board_file to an absolute path on load. Writing a
        // BoardConfig back would persist this machine's absolute path into a file
        // the rest of the team shares.
        var path = WriteConfig(Minimal);

        BoardConfigWriter.WriteIterations(path, [new IterationConfig("Sprint 1", null, null, [])]);

        Assert.Contains("\"board_file\": \"docs/backlog.md\"", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void AKeyThisBuildDoesNotUnderstandSurvivesTheWrite()
    {
        // The schema forbids unknown keys today, so the guarantee is proven with a
        // key the validator knows but this writer never touches. If the schema
        // later admits extensions, this is the behaviour they will rely on.
        var path = WriteConfig("""
            {
              "org": "contoso",
              "project": "widgets",
              "code_prefix": "PROJ",
              "team": "Widgets Team",
              "states": { "done": "Closed" }
            }
            """);

        BoardConfigWriter.WriteAssignees(
            path, new Dictionary<string, IReadOnlyList<string>> { ["ada@example.com"] = ["PROJ-101"] });

        var reloaded = BoardConfig.Load(path).Value;
        Assert.Equal("Widgets Team", reloaded.Team);
        Assert.Equal("Closed", reloaded.States["done"]);
    }

    [Fact]
    public void IterationDatesThatAreNotSetAreAbsentRatherThanNull()
    {
        var path = WriteConfig(Minimal);

        BoardConfigWriter.WriteIterations(path, [new IterationConfig("Sprint 1", null, null, ["PROJ-101"])]);

        var text = File.ReadAllText(path);
        Assert.DoesNotContain("null", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"start\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void IssueCodesAreUpperCasedAndDeduplicated()
    {
        var path = WriteConfig(Minimal);

        BoardConfigWriter.WriteIterations(
            path, [new IterationConfig("Sprint 1", null, null, ["proj-101", "PROJ-101", " proj-102 "])]);

        var codes = Assert.Single(BoardConfig.Load(path).Value.Iterations).Items;
        Assert.Equal(["PROJ-101", "PROJ-102"], codes);
    }

    [Fact]
    public void AssigneesAreWrittenInAStableOrderSoASaveThatChangedNothingChangesNothing()
    {
        var path = WriteConfig(Minimal);
        var assignees = new Dictionary<string, IReadOnlyList<string>>
        {
            ["zoe@example.com"] = ["PROJ-102"],
            ["ada@example.com"] = ["PROJ-103", "PROJ-101"],
        };

        BoardConfigWriter.WriteAssignees(path, assignees);
        var first = File.ReadAllText(path);

        BoardConfigWriter.WriteAssignees(path, assignees);
        var second = File.ReadAllText(path);

        Assert.Equal(first, second);
        Assert.True(
            first.IndexOf("ada@example.com", StringComparison.Ordinal)
            < first.IndexOf("zoe@example.com", StringComparison.Ordinal));
    }

    [Fact]
    public void TwoIterationsWithOneNameAreRefusedBecauseTheyWouldBeOneIterationPath()
    {
        var path = WriteConfig(Minimal);

        var written = BoardConfigWriter.WriteIterations(
            path,
            [
                new IterationConfig("Sprint 1", null, null, ["PROJ-101"]),
                new IterationConfig("sprint 1", null, null, ["PROJ-102"]),
            ]);

        Assert.Equal("config.duplicate_iteration", written.Error!.Code);
        Assert.DoesNotContain("iterations", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnnamedIterationIsRefused()
    {
        var path = WriteConfig(Minimal);

        var written = BoardConfigWriter.WriteIterations(
            path, [new IterationConfig("   ", null, null, [])]);

        Assert.Equal("config.unnamed_iteration", written.Error!.Code);
    }

    [Fact]
    public void AnUnnamedAssigneeIsRefused()
    {
        var path = WriteConfig(Minimal);

        var written = BoardConfigWriter.WriteAssignees(
            path, new Dictionary<string, IReadOnlyList<string>> { ["  "] = ["PROJ-101"] });

        Assert.Equal("config.unnamed_assignee", written.Error!.Code);
    }

    [Fact]
    public void WritingToAMissingConfigIsANotFoundRatherThanACreatedFile()
    {
        // A profile described in onboarding has no file on disk. Silently creating
        // one at a guessed path would put the team's config somewhere nobody looks.
        var missing = Path.Combine(_directory, "nowhere", "board.config.json");

        var written = BoardConfigWriter.WriteIterations(missing, []);

        Assert.Equal("config.not_found", written.Error!.Code);
        Assert.False(File.Exists(missing));
    }

    [Fact]
    public void AConfigThatIsNotJsonIsReportedRatherThanOverwritten()
    {
        var path = WriteConfig("{ this is not json");

        var written = BoardConfigWriter.WriteIterations(path, []);

        Assert.Equal("config.invalid_json", written.Error!.Code);
        Assert.Equal("{ this is not json", File.ReadAllText(path));
    }

    [Fact]
    public void TheWrittenConfigIsValidatedBeforeItReplacesTheFile()
    {
        // Finding out on the next open that the app wrote an unreadable config is
        // too late to recover the one it replaced.
        var path = WriteConfig(Minimal);
        var before = File.ReadAllText(path);

        var written = BoardConfigWriter.WriteAssignees(
            path, new Dictionary<string, IReadOnlyList<string>>());

        Assert.True(written.IsSuccess);
        Assert.NotEqual(before, File.ReadAllText(path));
        Assert.True(BoardConfig.Load(path).IsSuccess);
    }

    [Fact]
    public void NoTemporaryFileIsLeftBesideTheConfig()
    {
        var path = WriteConfig(Minimal);

        BoardConfigWriter.WriteIterations(path, [new IterationConfig("Sprint 1", null, null, [])]);

        Assert.Empty(Directory.GetFiles(_directory, "*.tmp-*"));
    }
}
