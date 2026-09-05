using System.Text.RegularExpressions;
using AdoBoardSync.Core.Markdown;
using AdoBoardSync.Desktop.Preview;

namespace AdoBoardSync.Desktop.Tests;

/// <summary>
/// The formatter exists so a person can read the markup. It must never change
/// what the markup says — the pane it feeds sits beside a Plan that writes.
/// </summary>
public class HtmlLayoutTests
{
    private static string Format(params string[] lines) =>
        HtmlLayout.Format(MarkdownHtml.ToHtml(lines));

    private static IReadOnlyList<string> Tags(string html) =>
        [.. Regex.Matches(html, @"<[^>]+>").Select(m => m.Value)];

    [Fact]
    public void NothingInBecomesNothingOut()
    {
        Assert.Equal(string.Empty, HtmlLayout.Format(null));
        Assert.Equal(string.Empty, HtmlLayout.Format("   "));
    }

    [Fact]
    public void AParagraphStaysOnOneLine()
    {
        Assert.Equal("<p>Just a sentence.</p>", Format("Just a sentence."));
    }

    [Fact]
    public void InlineTagsNeverBreakTheLine()
    {
        var formatted = Format("Plain **bold** and *italic* and `code`.");

        Assert.Single(formatted.Split('\n'));
        Assert.Contains("<b>bold</b>", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void ListItemsAreIndentedInsideTheirList()
    {
        var formatted = Format("- First", "- Second");

        Assert.Equal(
            [
                "<ul>",
                "  <li>First</li>",
                "  <li>Second</li>",
                "</ul>",
            ],
            formatted.Split('\n'));
    }

    [Fact]
    public void ANestedListIsIndentedInsideItsParentItem()
    {
        var formatted = Format("- Parent", "  - Child", "- Sibling");

        Assert.Equal(
            [
                "<ul>",
                "  <li>Parent",
                "  <ul>",
                "    <li>Child</li>",
                "  </ul></li>",
                "  <li>Sibling</li>",
                "</ul>",
            ],
            formatted.Split('\n'));
    }

    [Fact]
    public void TableCellsGetALinePerCell()
    {
        var formatted = Format(
            "| Field | Meaning |",
            "| --- | --- |",
            "| org | The organisation |");

        var lines = formatted.Split('\n');

        Assert.StartsWith("<table", lines[0], StringComparison.Ordinal);
        Assert.Equal("  <tr>", lines[1]);
        Assert.Contains(">Field</th>", lines[2], StringComparison.Ordinal);
        Assert.StartsWith("    <th", lines[2], StringComparison.Ordinal);
        Assert.Equal("</table>", lines[^1]);
    }

    /// <summary>
    /// The assertion the pane's caption depends on: formatting adds whitespace
    /// between tags and nothing else. Same tags, same order, same text.
    /// </summary>
    [Theory]
    [InlineData("Just prose.")]
    [InlineData("Prose with **bold**, *italic* and `code`.")]
    [InlineData("- One\n- Two")]
    [InlineData("- Parent\n  - Child\n- Sibling")]
    [InlineData("Above\n\n---\n\nBelow")]
    [InlineData("| A | B |\n| --- | --- |\n| 1 | 2 |")]
    [InlineData("Escaped <div> & \"quotes\".")]
    public void FormattingChangesNothingButWhitespace(string source)
    {
        var original = MarkdownHtml.ToHtml(source.Split('\n'));
        var formatted = HtmlLayout.Format(original);

        Assert.Equal(Tags(original), Tags(formatted));
        Assert.Equal(MarkdownHtml.Normalize(original), MarkdownHtml.Normalize(formatted));
    }

    /// <summary>
    /// A space between two inline tags is part of the sentence, unlike the
    /// converter's newline joins between its own output lines.
    /// </summary>
    [Fact]
    public void ASpaceBetweenInlineTagsSurvives()
    {
        var formatted = HtmlLayout.Format("<p><b>a</b> <i>b</i></p>");

        Assert.Equal("<p><b>a</b> <i>b</i></p>", formatted);
    }
}
