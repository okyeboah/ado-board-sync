using AdoBoardSync.Core.Markdown;

namespace AdoBoardSync.Core.Tests;

/// <summary>
/// States the conversion rules the preview pane relies on. The parity suite proves
/// the .NET output equals the CLI's; these tests say what that output should be, so
/// a rule change has to be made deliberately in both implementations.
/// </summary>
public class MarkdownHtmlTests
{
    private static string Convert(string markdown) => MarkdownHtml.ToHtml(markdown.Split('\n'));

    [Fact]
    public void EscapingRunsBeforeInlineMarkdown() =>
        Assert.Equal("<p>a &amp; b &lt;tag&gt;</p>", Convert("a & b <tag>"));

    [Fact]
    public void BoldItalicAndCodeBecomeTags() =>
        Assert.Equal(
            "<p><b>bold</b> <i>italics</i> <code>code</code></p>",
            Convert("**bold** *italics* `code`"));

    [Fact]
    public void CodeSpansAreNotReachedIntoByTheItalicsPass()
    {
        // A backlog says `SharedKernel.*` and `*Command`. Without protecting code
        // spans first, the italics pass pairs those asterisks across the two spans
        // and emits interleaved i/code tags that no browser can nest.
        var html = Convert("`SharedKernel.*` and `*Command`");

        Assert.Equal("<p><code>SharedKernel.*</code> and <code>*Command</code></p>", html);
        Assert.Empty(HtmlBalance.Problems(html));
    }

    [Fact]
    public void AWrappedLineJoinsTheBlockAboveIt() =>
        Assert.Equal("<p>one two three</p>", Convert("one\ntwo\nthree"));

    [Fact]
    public void ABlankLineSeparatesTwoParagraphs() =>
        Assert.Equal("<p>one</p>\n<p>two</p>", Convert("one\n\ntwo"));

    [Fact]
    public void NestedBulletsNestByIndentation() =>
        Assert.Equal(
            "<ul>\n<li>one\n<ul>\n<li>deeper</li>\n</ul></li>\n<li>two</li>\n</ul>",
            Convert("- one\n  * deeper\n- two"));

    [Fact]
    public void ARuleClosesAnOpenList()
    {
        var html = Convert("- one\n---");

        Assert.Equal("<ul>\n<li>one</li>\n</ul>\n<hr>", html);
        Assert.Empty(HtmlBalance.Problems(html));
    }

    [Fact]
    public void TheSeparatorRowCarriesNoData()
    {
        var html = Convert("| A | B |\n| --- | --- |\n| 1 | 2 |");

        Assert.Equal(2, CountOccurrences(html, "<tr>"));
        Assert.Contains("<th", html, StringComparison.Ordinal);
        Assert.DoesNotContain("---", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ATableWithoutASeparatorStillGetsAHeaderRow()
    {
        var html = Convert("| A | B |\n| 1 | 2 |");

        Assert.Equal(2, CountOccurrences(html, "<tr>"));
        Assert.Contains("<th", html, StringComparison.Ordinal);
    }

    [Fact]
    public void TablesCarryInlineBordersBecauseAzureDevOpsAppliesNoStylesheet()
    {
        var html = Convert("| A |\n| 1 |");

        Assert.Contains("border-collapse:collapse", html, StringComparison.Ordinal);
        Assert.Contains("border:1px solid #c8c8c8", html, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryGeneratedFixtureProducesBalancedMarkup()
    {
        var html = Convert("""
            Intro paragraph.

            - one
              * deeper
                - deepest
            - two

            | A | B |
            | --- | --- |
            | 1 | 2 |

            ---

            Closing paragraph.
            """);

        Assert.Empty(HtmlBalance.Problems(html));
    }

    [Fact]
    public void PlainStripsCodeAndBoldAndCollapsesWhitespace() =>
        Assert.Equal("run the EventStore now", MarkdownHtml.Plain("run  the `EventStore` **now**"));

    [Fact]
    public void InlineKeepsBoldButNotItalics() =>
        Assert.Equal("<b>bold</b> *not italics*", MarkdownHtml.Inline("**bold** *not italics*"));

    [Fact]
    public void NormalizeReducesMarkupToComparableText() =>
        Assert.Equal("a & b", MarkdownHtml.Normalize("<p>A  &amp;  <b>B</b></p>"));

    [Fact]
    public void NormalizeTreatsNullAsEmpty() =>
        Assert.Equal(string.Empty, MarkdownHtml.Normalize(null));

    private static int CountOccurrences(string haystack, string needle) =>
        haystack.AsSpan().Count(needle.AsSpan());
}
