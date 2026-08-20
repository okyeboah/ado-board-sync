using AdoBoardSync.Core.Markdown;
using AdoBoardSync.TestKit;

namespace AdoBoardSync.Parity.Tests;

/// <summary>
/// Proves the .NET conversion produces exactly what the CLI produces.
///
/// The preview pane and the description written to Azure DevOps both come from
/// <see cref="MarkdownHtml"/>. If it drifts from <c>htmlfmt.py</c>, the preview
/// stops describing what the board will actually show, so any difference here is
/// a release blocker rather than a warning.
/// </summary>
public class MarkdownHtmlParityTests
{
    public static TheoryData<string, string> AllFixtures => Fixtures.AllFiles;

    public static TheoryData<string> MarkupFixtures => Fixtures.MarkupFiles;

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void ToHtml_MatchesThePythonImplementation(string directory, string fixture)
    {
        var text = File.ReadAllText(RepoPaths.Fixture(directory, fixture));

        var expected = PythonReference.Text("html", text);
        var actual = MarkdownHtml.ToHtml(text.Split('\n'));

        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(MarkupFixtures))]
    public void Plain_MatchesThePythonImplementation(string fixture)
    {
        var text = File.ReadAllText(RepoPaths.Fixture(Fixtures.Markup, fixture));

        Assert.Equal(PythonReference.Text("plain", text), MarkdownHtml.Plain(text));
    }

    [Theory]
    [MemberData(nameof(MarkupFixtures))]
    public void Inline_MatchesThePythonImplementation(string fixture)
    {
        var text = File.ReadAllText(RepoPaths.Fixture(Fixtures.Markup, fixture));

        Assert.Equal(PythonReference.Text("inline", text), MarkdownHtml.Inline(text));
    }

    [Theory]
    [MemberData(nameof(MarkupFixtures))]
    public void Normalize_MatchesThePythonImplementationOnRenderedHtml(string fixture)
    {
        var html = MarkdownHtml.ToHtml(
            File.ReadAllText(RepoPaths.Fixture(Fixtures.Markup, fixture)).Split('\n'));

        Assert.Equal(PythonReference.Text("norm", html), MarkdownHtml.Normalize(html));
    }

    [Theory]
    [InlineData("plain text, no tags at all")]
    [InlineData("<p>balanced</p>")]
    [InlineData("<b>interleaved</i>")]
    [InlineData("<ul><li>never closed")]
    [InlineData("</p> closes nothing")]
    [InlineData("<hr> and <br> and <img src=\"x\"> are void")]
    [InlineData("<p>a <!-- comment --> b</p>")]
    [InlineData("<!DOCTYPE html><p>x</p>")]
    [InlineData("a < b and c > d")]
    [InlineData("<table><tr><td>cell</td></tr></table>")]
    [InlineData("<P>Uppercase tags are lowercased</P>")]
    public void Problems_MatchThePythonImplementation(string markup) =>
        Assert.Equal(PythonReference.Problems(markup), HtmlBalance.Problems(markup));

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Problems_MatchThePythonImplementationOnRenderedHtml(string directory, string fixture)
    {
        var html = MarkdownHtml.ToHtml(
            File.ReadAllText(RepoPaths.Fixture(directory, fixture)).Split('\n'));

        Assert.Equal(PythonReference.Problems(html), HtmlBalance.Problems(html));
    }
}
