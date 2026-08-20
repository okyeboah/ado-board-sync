using AdoBoardSync.Core.Markdown;

namespace AdoBoardSync.Core.Tests;

/// <summary>
/// Covers the check the editor uses to block Apply. Azure DevOps stores the
/// description as HTML and shows it unchanged, so anything this misses reaches
/// a reader's browser as broken markup.
/// </summary>
public class HtmlBalanceTests
{
    [Fact]
    public void WellFormedMarkupHasNoProblems() =>
        Assert.Empty(HtmlBalance.Problems("<p>text <b>bold</b> and <i>italics</i></p>"));

    [Fact]
    public void PlainTextHasNoProblems() =>
        Assert.Empty(HtmlBalance.Problems("no markup at all"));

    [Fact]
    public void AnUnclosedTagIsReported() =>
        Assert.Equal(["<b> never closes"], HtmlBalance.Problems("<b>open forever"));

    [Fact]
    public void InterleavedTagsAreReported() =>
        Assert.Equal(
            ["</b> closes an open <i>", "<b> never closes"],
            HtmlBalance.Problems("<b><i>x</b></i>"));

    [Fact]
    public void AStrayClosingTagIsReported() =>
        Assert.Equal(["</p> closes nothing"], HtmlBalance.Problems("</p>"));

    [Theory]
    [InlineData("<hr>")]
    [InlineData("<br>")]
    [InlineData("<img src=\"x.png\">")]
    public void VoidTagsNeverNeedClosing(string markup) =>
        Assert.Empty(HtmlBalance.Problems(markup));

    [Fact]
    public void CommentsAreSkipped() =>
        Assert.Empty(HtmlBalance.Problems("<p><!-- <b>not a real tag</b> --></p>"));

    [Fact]
    public void DeclarationsAreSkipped() =>
        Assert.Empty(HtmlBalance.Problems("<!DOCTYPE html><p>x</p>"));

    [Fact]
    public void ABareAngleBracketIsTreatedAsText() =>
        Assert.Empty(HtmlBalance.Problems("a < b and c > d"));

    [Fact]
    public void TagNamesAreCaseInsensitive() =>
        Assert.Empty(HtmlBalance.Problems("<P>text</p>"));

    [Fact]
    public void AttributesDoNotAffectTagMatching() =>
        Assert.Empty(HtmlBalance.Problems("""<table style="border-collapse:collapse;"><tr><td>x</td></tr></table>"""));
}
