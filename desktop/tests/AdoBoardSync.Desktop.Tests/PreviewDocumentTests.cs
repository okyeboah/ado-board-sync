using AdoBoardSync.Core.Markdown;
using AdoBoardSync.Desktop.Preview;

namespace AdoBoardSync.Desktop.Tests;

/// <summary>
/// The preview is parsed from the markup the app would send, so these assert
/// against <see cref="MarkdownHtml.ToHtml"/>'s real output rather than against
/// hand-written HTML that could drift from it.
/// </summary>
public class PreviewDocumentTests
{
    private static PreviewDocument Parse(params string[] lines) =>
        PreviewDocument.Parse(MarkdownHtml.ToHtml(lines));

    private static string TextOf(PreviewBlock block) =>
        string.Concat(block.Runs.Select(r => r.Text));

    [Fact]
    public void AnEmptyDescriptionHasNoBlocks()
    {
        Assert.True(PreviewDocument.Parse(null).IsEmpty);
        Assert.True(PreviewDocument.Parse(string.Empty).IsEmpty);
        Assert.True(Parse().IsEmpty);
    }

    [Fact]
    public void ProseBecomesAParagraph()
    {
        var document = Parse("Just a sentence.");

        var block = Assert.Single(document.Blocks);
        Assert.Equal(PreviewBlockKind.Paragraph, block.Kind);
        Assert.Equal("Just a sentence.", TextOf(block));
    }

    [Fact]
    public void BoldItalicAndCodeArriveAsFormattedRuns()
    {
        var document = Parse("Plain **bold** and *italic* and `code`.");

        var runs = Assert.Single(document.Blocks).Runs;

        Assert.Contains(runs, r => r.Text == "bold" && r is { Bold: true, Italic: false, Code: false });
        Assert.Contains(runs, r => r.Text == "italic" && r is { Italic: true, Bold: false, Code: false });
        Assert.Contains(runs, r => r.Text == "code" && r is { Code: true, Bold: false, Italic: false });

        // Nothing is dropped: the rendered text is the plain text of the source.
        Assert.Equal("Plain bold and italic and code.", string.Concat(runs.Select(r => r.Text)));
    }

    [Fact]
    public void BulletsBecomeBulletBlocksAtDepthZero()
    {
        var document = Parse("- First", "- Second");

        Assert.Equal(2, document.Blocks.Count);
        Assert.All(document.Blocks, b =>
        {
            Assert.Equal(PreviewBlockKind.Bullet, b.Kind);
            Assert.Equal(0, b.Depth);
        });
        Assert.Equal(["First", "Second"], document.Blocks.Select(TextOf));
    }

    /// <summary>
    /// The converter closes a parent <c>&lt;li&gt;</c> only after the nested list,
    /// so a naive parser folds the child into its parent or loses the depth.
    /// </summary>
    [Fact]
    public void ANestedBulletKeepsItsParentAndItsDepth()
    {
        var document = Parse("- Parent", "  - Child", "- Sibling");

        Assert.Equal(
            [("Parent", 0), ("Child", 1), ("Sibling", 0)],
            document.Blocks.Select(b => (TextOf(b), b.Depth)));
    }

    [Fact]
    public void AHorizontalRuleBecomesARuleBlock()
    {
        var document = Parse("Above", "", "---", "", "Below");

        Assert.Equal(
            [PreviewBlockKind.Paragraph, PreviewBlockKind.Rule, PreviewBlockKind.Paragraph],
            document.Blocks.Select(b => b.Kind));
    }

    [Fact]
    public void ATableKeepsItsHeaderRowAndItsCells()
    {
        var document = Parse(
            "| Field | Meaning |",
            "| --- | --- |",
            "| org | The organisation |");

        var table = Assert.Single(document.Blocks);
        Assert.Equal(PreviewBlockKind.Table, table.Kind);
        Assert.Equal(2, table.Rows.Count);

        Assert.True(table.Rows[0].IsHeader);
        Assert.Equal(["Field", "Meaning"], table.Rows[0].Cells.Select(c => string.Concat(c.Select(r => r.Text))));

        Assert.False(table.Rows[1].IsHeader);
        Assert.Equal(["org", "The organisation"], table.Rows[1].Cells.Select(c => string.Concat(c.Select(r => r.Text))));
    }

    /// <summary>
    /// The converter escapes before it formats, so the preview has to unescape or
    /// a description mentioning a tag would read as literal <c>&amp;lt;</c>.
    /// </summary>
    [Fact]
    public void EscapedCharactersComeBackAsThemselves()
    {
        var document = Parse("Use <div> & \"quotes\" carefully.");

        Assert.Equal("Use <div> & \"quotes\" carefully.", TextOf(Assert.Single(document.Blocks)));
    }

    [Fact]
    public void AmpersandIsNotDecodedTwiceIntoATag()
    {
        // The source literally says "&lt;b&gt;", which escapes to "&amp;lt;b&amp;gt;".
        // Decoding the ampersand first would turn it back into a bold tag.
        var document = Parse("Write &lt;b&gt; to show a bold tag.");

        var text = TextOf(Assert.Single(document.Blocks));
        Assert.Equal("Write &lt;b&gt; to show a bold tag.", text);
        Assert.All(Assert.Single(document.Blocks).Runs, r => Assert.False(r.Bold));
    }

    /// <summary>
    /// A Task title is converted with <see cref="MarkdownHtml.Inline"/>, which
    /// emits formatted text with no block tag around it. Dropping text that
    /// arrives outside a block would leave every Task line blank.
    /// </summary>
    [Fact]
    public void ATaskTitleConvertedInlineStillProducesAParagraph()
    {
        var document = PreviewDocument.Parse(MarkdownHtml.Inline("Wire `ddi-api` and **ddi-worker**"));

        var block = Assert.Single(document.Blocks);
        Assert.Equal(PreviewBlockKind.Paragraph, block.Kind);
        Assert.Equal("Wire ddi-api and ddi-worker", TextOf(block));
        Assert.Contains(block.Runs, r => r.Text == "ddi-api" && r.Code);
        Assert.Contains(block.Runs, r => r.Text == "ddi-worker" && r.Bold);
    }

    /// <summary>
    /// The whole point of parsing the generated markup: every word the board would
    /// receive is a word the preview shows.
    /// </summary>
    [Fact]
    public void ThePreviewLosesNoTextFromTheGeneratedMarkup()
    {
        string[] lines =
        [
            "*Context line* with `code`.",
            "",
            "- A bullet with **bold**",
            "  - A nested bullet",
            "",
            "| A | B |",
            "| --- | --- |",
            "| 1 | 2 |",
        ];

        var html = MarkdownHtml.ToHtml(lines);
        var document = PreviewDocument.Parse(html);

        var previewWords = document.Blocks
            .SelectMany(b => b.Runs.Concat(b.Rows.SelectMany(r => r.Cells.SelectMany(c => c))))
            .SelectMany(r => r.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Order()
            .ToList();

        var markupWords = MarkdownHtml.Normalize(html)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Order()
            .ToList();

        Assert.Equal(markupWords, previewWords.Select(w => w.ToLowerInvariant()).Order().ToList());
    }
}
