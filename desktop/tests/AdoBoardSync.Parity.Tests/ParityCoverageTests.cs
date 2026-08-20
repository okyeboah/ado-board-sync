using System.Text.Json;
using AdoBoardSync.TestKit;

namespace AdoBoardSync.Parity.Tests;

/// <summary>
/// Guards the completeness of the parity suite itself.
///
/// A parity suite reports green for the inputs it happens to feed, so a construct
/// no fixture contains is not verified — it is merely unexamined, which reads the
/// same from the outside. These tests fail when a configuration key or a
/// documented Markdown construct has no scenario behind it.
///
/// This caught a real gap: <c>epic_heading_regex</c>, the only place a
/// caller-supplied regex reaches the parser and the sole reason
/// <c>PythonCompat.MatchAtStart</c> exists, had no scenario at all.
/// </summary>
public class ParityCoverageTests
{
    [Fact]
    public void EveryConfigurationKeyHasAParityScenario()
    {
        using var schema = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(RepoPaths.Root, "board.config.schema.json")));

        var scenarios = ScenarioSource();

        var unexercised = schema.RootElement
            .GetProperty("properties")
            .EnumerateObject()
            .Select(property => property.Name)
            .Where(name => !scenarios.Contains($"\"{name}\"", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            unexercised.Count == 0,
            $"No parity scenario sets: {string.Join(", ", unexercised)}. "
            + "Add one, or the key's resolution is compared against nothing.");
    }

    [Theory]
    [InlineData("**bold**", "**")]
    [InlineData("italics", "*italics*")]
    [InlineData("inline code", "`")]
    [InlineData("dash bullet", "\n- ")]
    [InlineData("asterisk bullet", "\n* ")]
    [InlineData("plus bullet", "\n+ ")]
    [InlineData("nested bullet", "\n  * ")]
    [InlineData("table separator row", "| --- |")]
    [InlineData("dash rule", "\n---\n")]
    [InlineData("asterisk rule", "\n***\n")]
    [InlineData("underscore rule", "\n___\n")]
    [InlineData("escapable characters", "&")]
    [InlineData("epic heading", "\n## Epic ")]
    [InlineData("stop heading", "\n## Appendix")]
    public void EveryDocumentedMarkdownConstructAppearsInAFixture(string construct, string marker)
    {
        var fixtures = string.Join(
            "\n",
            Directory.EnumerateFiles(RepoPaths.Fixtures, "*.md", SearchOption.AllDirectories)
                .Select(File.ReadAllText));

        Assert.True(
            fixtures.Contains(marker, StringComparison.Ordinal),
            $"No fixture contains {construct}, so its conversion is never compared to the CLI.");
    }

    /// <summary>
    /// The parity scenarios as text. Read from source because the keys a scenario
    /// sets are literals in it; there is no runtime record of what a theory covered.
    /// </summary>
    private static string ScenarioSource()
    {
        var directory = Path.Combine(RepoPaths.Root, "desktop", "tests");
        var sources = new[]
        {
            Path.Combine(directory, "AdoBoardSync.Parity.Tests", "BoardConfigParityTests.cs"),
            Path.Combine(directory, "AdoBoardSync.Parity.Tests", "BacklogParserParityTests.cs"),
            Path.Combine(directory, "AdoBoardSync.TestKit", "TempBoardProfile.cs")
        };

        foreach (var source in sources)
        {
            Assert.True(File.Exists(source), $"Expected parity scenario source at {source}.");
        }

        return string.Join("\n", sources.Select(File.ReadAllText));
    }
}
