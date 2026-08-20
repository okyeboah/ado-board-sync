using System.Text.Json;
using System.Text.Json.Nodes;
using AdoBoardSync.Core.Backlog;
using AdoBoardSync.Core.Configuration;
using AdoBoardSync.TestKit;

namespace AdoBoardSync.Parity.Tests;

/// <summary>
/// Proves the .NET parser reads a backlog exactly as the CLI reads it.
///
/// The Issue code is the stable identity that matches a backlog item to a board
/// work item. If the two parsers disagree about which lines are Issues, Tasks, or
/// description, every downstream plan is computed against a different backlog than
/// the CLI would use.
/// </summary>
public class BacklogParserParityTests
{
    public static TheoryData<string> BacklogFixtures => Fixtures.BacklogFiles;

    [Theory]
    [MemberData(nameof(BacklogFixtures))]
    public void Parse_MatchesThePythonImplementation(string fixture)
    {
        var backlog = RepoPaths.Fixture(Fixtures.Backlog, fixture);
        using var profile = TempBoardProfile.Create(backlog);

        using var reference = PythonReference.WithConfig("parse", profile.ConfigPath);
        var config = BoardConfig.Load(profile.ConfigPath);
        Assert.True(config.IsSuccess, config.Error?.SafeMessage);

        var actual = BacklogParser.Parse(config.Value, File.ReadAllText(backlog));

        Assert.Equal(
            Canonical(reference.RootElement.GetProperty("items")),
            Canonical(actual));
    }

    [Theory]
    [MemberData(nameof(BacklogFixtures))]
    public void TasksByCode_MatchesThePythonImplementation(string fixture)
    {
        var backlog = RepoPaths.Fixture(Fixtures.Backlog, fixture);
        using var profile = TempBoardProfile.Create(backlog);

        using var reference = PythonReference.WithConfig("tasks", profile.ConfigPath);
        var config = BoardConfig.Load(profile.ConfigPath);
        Assert.True(config.IsSuccess, config.Error?.SafeMessage);

        var actual = BacklogParser.TasksByCode(config.Value, File.ReadAllText(backlog));
        var expected = reference.RootElement.GetProperty("tasks");

        var expectedCodes = expected.EnumerateObject().Select(p => p.Name).OrderBy(c => c, StringComparer.Ordinal);
        Assert.Equal(expectedCodes, actual.Keys.OrderBy(c => c, StringComparer.Ordinal));

        foreach (var property in expected.EnumerateObject())
        {
            var expectedBullets = property.Value.EnumerateArray().Select(e => e.GetString()!).ToList();
            Assert.Equal(expectedBullets, actual[property.Name]);
        }
    }

    /// <summary>
    /// Renders either side's items as one canonical JSON string, so a failure shows
    /// the differing item rather than "two object graphs are not equal".
    /// </summary>
    private static string Canonical(JsonElement pythonItems)
    {
        var array = new JsonArray();
        foreach (var item in pythonItems.EnumerateArray())
        {
            var level = item.GetProperty("level").GetString()!;
            array.Add(Node(
                level,
                item.GetProperty("title").GetString()!,
                level == "issue" ? item.GetProperty("code").GetString() : null,
                [.. item.GetProperty("desc_lines").EnumerateArray().Select(e => e.GetString()!)],
                level == "issue"
                    ? [.. item.GetProperty("bullets").EnumerateArray().Select(e => e.GetString()!)]
                    : null));
        }

        return array.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static string Canonical(IReadOnlyList<BacklogItem> items)
    {
        var array = new JsonArray();
        foreach (var item in items)
        {
            var isIssue = item.Level == BacklogLevel.Issue;
            array.Add(Node(
                isIssue ? "issue" : "epic",
                item.Title,
                isIssue ? item.Code : null,
                item.DescriptionLines,
                isIssue ? item.Bullets : null));
        }

        return array.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonObject Node(
        string level,
        string title,
        string? code,
        IReadOnlyList<string> descriptionLines,
        IReadOnlyList<string>? bullets)
    {
        var node = new JsonObject
        {
            ["level"] = level,
            ["title"] = title,
            ["desc_lines"] = new JsonArray([.. descriptionLines.Select(l => JsonValue.Create(l))])
        };

        if (code is not null)
        {
            node["code"] = code;
        }

        if (bullets is not null)
        {
            node["bullets"] = new JsonArray([.. bullets.Select(b => JsonValue.Create(b))]);
        }

        return node;
    }
}
