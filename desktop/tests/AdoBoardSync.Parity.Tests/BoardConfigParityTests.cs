using System.Text.Json;
using System.Text.Json.Nodes;
using AdoBoardSync.Core.Configuration;
using AdoBoardSync.TestKit;

namespace AdoBoardSync.Parity.Tests;

/// <summary>
/// Proves the .NET config loader resolves the same values as the CLI.
///
/// Both read the same <c>board.config.json</c>, so a defaulting difference — a
/// different work-item type name, a different task title cap — would make the
/// desktop app plan against settings the CLI never used.
/// </summary>
public class BoardConfigParityTests
{
    [Fact]
    public void Defaults_MatchThePythonImplementation() =>
        AssertMatchesPython(customise: null);

    [Fact]
    public void ExplicitOverrides_MatchThePythonImplementation() =>
        AssertMatchesPython(config =>
        {
            config["api_version"] = "7.2";
            config["csv_file"] = "artifacts/items.csv";
            config["types"] = new JsonObject
            {
                ["epic"] = "Epic",
                ["story"] = "User Story",
                ["task"] = "Task"
            };
            config["states"] = new JsonObject { ["done"] = "Closed" };
            config["pat_env"] = "CONTOSO_PAT";
            config["pat_file"] = ".contoso_pat";
            config["task_title_max"] = 120;
            config["max_retries"] = 5;
            config["backoff"] = 2.5;
            config["timeout"] = 45;
            config["team"] = "DemoProject Team";
            config["stop_headings"] = new JsonArray("## Appendix", "## Register");
        });

    [Fact]
    public void PartialTypeOverride_KeepsTheUnsetDefaults() =>
        AssertMatchesPython(config =>
            config["types"] = new JsonObject { ["story"] = "Product Backlog Item" });

    [Fact]
    public void IterationsAndAssignees_MatchThePythonImplementation() =>
        AssertMatchesPython(config =>
        {
            config["iterations"] = new JsonArray(
                new JsonObject
                {
                    ["name"] = "Sprint 1",
                    ["start"] = "2026-06-29",
                    ["finish"] = "2026-07-10",
                    ["items"] = new JsonArray("PROJ-101", "PROJ-102")
                },
                new JsonObject
                {
                    ["name"] = "Sprint 2",
                    ["items"] = new JsonArray("PROJ-201")
                });
            config["assignees"] = new JsonObject
            {
                ["alice@example.com"] = new JsonArray("PROJ-101"),
                ["bob@example.com"] = new JsonArray("PROJ-201", "PROJ-102")
            };
        });

    private static void AssertMatchesPython(Action<JsonObject>? customise)
    {
        var backlog = RepoPaths.Fixture(Fixtures.Backlog, "standard.md");
        using var profile = TempBoardProfile.Create(backlog, customise);

        using var reference = PythonReference.WithConfig("config", profile.ConfigPath);
        var expected = reference.RootElement;

        var loaded = BoardConfig.Load(profile.ConfigPath);
        Assert.True(loaded.IsSuccess, loaded.Error?.SafeMessage);
        var config = loaded.Value;

        Assert.Equal(expected.GetProperty("org").GetString(), config.Org);
        Assert.Equal(expected.GetProperty("project").GetString(), config.Project);
        Assert.Equal(expected.GetProperty("code_prefix").GetString(), config.CodePrefix);
        Assert.Equal(expected.GetProperty("api_version").GetString(), config.ApiVersion);
        Assert.Equal(expected.GetProperty("board_file").GetString(), config.BoardFile);
        Assert.Equal(expected.GetProperty("csv_file").GetString(), config.CsvFile);
        Assert.Equal(expected.GetProperty("pat_env").GetString(), config.PatEnv);
        Assert.Equal(expected.GetProperty("pat_file").GetString(), config.PatFile);
        Assert.Equal(expected.GetProperty("task_title_max").GetInt32(), config.TaskTitleMax);
        Assert.Equal(expected.GetProperty("max_retries").GetInt32(), config.MaxRetries);
        Assert.Equal(expected.GetProperty("backoff").GetDouble(), config.Backoff);
        Assert.Equal(expected.GetProperty("timeout").GetInt32(), config.Timeout);
        Assert.Equal(expected.GetProperty("issue_code_pattern").GetString(), config.IssueCodeRegex.ToString());
        Assert.Equal(expected.GetProperty("base_url").GetString(), config.BaseUrl);
        Assert.Equal(expected.GetProperty("org_url").GetString(), config.OrgUrl);

        var team = expected.GetProperty("team");
        Assert.Equal(team.ValueKind == JsonValueKind.Null ? null : team.GetString(), config.Team);

        AssertStringMap(expected.GetProperty("types"), config.Types);
        AssertStringMap(expected.GetProperty("states"), config.States);
        AssertStringList(expected.GetProperty("stop_headings"), config.StopHeadings);

        AssertIterations(expected.GetProperty("iterations"), config.Iterations);
        AssertAssignees(expected.GetProperty("assignees"), config.Assignees);
    }

    private static void AssertStringMap(JsonElement expected, IReadOnlyDictionary<string, string> actual) =>
        AssertMap(expected, actual, (value, entry) => Assert.Equal(value.GetString(), entry));

    /// <summary>
    /// Compares one JSON object against a dictionary: same key set, then the caller's
    /// assertion per value. Key comparison is the part that is identical for every
    /// map, and the part most likely to be forgotten.
    /// </summary>
    private static void AssertMap<T>(
        JsonElement expected,
        IReadOnlyDictionary<string, T> actual,
        Action<JsonElement, T> assertValue)
    {
        Assert.Equal(
            expected.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal),
            actual.Keys.OrderBy(n => n, StringComparer.Ordinal));

        foreach (var property in expected.EnumerateObject())
        {
            assertValue(property.Value, actual[property.Name]);
        }
    }

    private static void AssertStringList(JsonElement expected, IReadOnlyList<string> actual) =>
        Assert.Equal(expected.EnumerateArray().Select(e => e.GetString()!).ToList(), actual);

    private static void AssertIterations(JsonElement expected, IReadOnlyList<IterationConfig> actual)
    {
        var expectedList = expected.EnumerateArray().ToList();
        Assert.Equal(expectedList.Count, actual.Count);

        for (var i = 0; i < expectedList.Count; i++)
        {
            Assert.Equal(expectedList[i].GetProperty("name").GetString(), actual[i].Name);
            Assert.Equal(OptionalString(expectedList[i], "start"), actual[i].Start);
            Assert.Equal(OptionalString(expectedList[i], "finish"), actual[i].Finish);
            AssertStringList(expectedList[i].GetProperty("items"), actual[i].Items);
        }
    }

    private static void AssertAssignees(
        JsonElement expected,
        IReadOnlyDictionary<string, IReadOnlyList<string>> actual) =>
        AssertMap(expected, actual, AssertStringList);

    private static string? OptionalString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetString()
            : null;
}
