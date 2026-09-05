using AdoBoardSync.Core.Backlog;
using AdoBoardSync.Core.Configuration;
using AdoBoardSync.TestKit;

namespace AdoBoardSync.Core.Tests;

/// <summary>
/// Pins the splice the editor's Save performs. The ranges come from the parser
/// itself, so the strongest property is a round trip: replacing every item's
/// block with its own lines must reproduce the file exactly — whatever the
/// line-ending style, whatever surrounds the blocks.
/// </summary>
public class BacklogSplicerTests
{
    private static BoardConfig Config()
    {
        var result = BoardConfig.Parse(
            """{"org": "o", "project": "p", "code_prefix": "PROJ"}""",
            Path.GetTempPath());
        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        return result.Value;
    }

    [Fact]
    public void TheBlockIsReplacedAndEverythingOutsideItSurvives()
    {
        const string markdown = """
            ## Epic 1 — First

            ### PROJ-101 · Edited
            old line

            ### PROJ-102 · Untouched
            stays

            """;

        var items = BacklogParser.Parse(Config(), markdown);
        var edited = items.Single(i => i.Code == "PROJ-101");

        var result = BacklogSplicer.ReplaceDescription(markdown, edited, "new line\n- a new task");

        Assert.Equal(
            """
            ## Epic 1 — First

            ### PROJ-101 · Edited
            new line
            - a new task

            ### PROJ-102 · Untouched
            stays

            """,
            result);
    }

    [Fact]
    public void AddingADescriptionToAnItemWithoutOne()
    {
        const string markdown = "## Epic 1 — First\n### PROJ-101 · Bare\n### PROJ-102 · Later\n";

        var items = BacklogParser.Parse(Config(), markdown);
        var bare = items.Single(i => i.Code == "PROJ-101");

        var result = BacklogSplicer.ReplaceDescription(markdown, bare, "Now described.");

        Assert.Equal(
            "## Epic 1 — First\n### PROJ-101 · Bare\nNow described.\n### PROJ-102 · Later\n",
            result);
    }

    [Fact]
    public void ClearingADescriptionRemovesExactlyTheBlock()
    {
        const string markdown = "## Epic 1 — First\n### PROJ-101 · Issue\nold\nmore\n### PROJ-102 · Later\n";

        var items = BacklogParser.Parse(Config(), markdown);
        var issue = items.Single(i => i.Code == "PROJ-101");

        var result = BacklogSplicer.ReplaceDescription(markdown, issue, string.Empty);

        Assert.Equal(
            "## Epic 1 — First\n### PROJ-101 · Issue\n### PROJ-102 · Later\n",
            result);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void TheLineEndingStyleIsPreserved(string eol)
    {
        var markdown = string.Join(eol, ["## Epic 1 — First", "### PROJ-101 · Issue", "old"]) + eol;

        var item = BacklogParser.Parse(Config(), markdown).Single(i => i.Code == "PROJ-101");

        var result = BacklogSplicer.ReplaceDescription(markdown, item, "new");

        Assert.Equal("## Epic 1 — First" + eol + "### PROJ-101 · Issue" + eol + "new" + eol, result);
    }

    [Fact]
    public void AMixedFileTakesItsCRLFStyleFromTheWhole()
    {
        // A single CRLF anywhere means the file is a CRLF file; a LF-only block
        // spliced into it must not flip the file's dominant style.
        const string markdown = "## Epic 1 — First\r\n### PROJ-101 · Issue\r\nold\nafter\r\n";

        var item = BacklogParser.Parse(Config(), markdown).Single(i => i.Code == "PROJ-101");

        var result = BacklogSplicer.ReplaceDescription(markdown, item, "new");

        // Both "old" and "after" are the item's description lines.
        Assert.Equal("## Epic 1 — First\r\n### PROJ-101 · Issue\r\nnew\r\n", result);
    }

    [Fact]
    public void TheBlankSeparatorBeforeTheNextHeadingSurvivesAnEdit()
    {
        // The parser keeps the block's trailing blank lines in DescriptionLines,
        // but they are the gap between items, not content: an edit that does not
        // retype them must not delete them (or shift every later item's range).
        const string markdown = "## Epic 1 — First\n### PROJ-101 · Issue\nold\n\n\n### PROJ-102 · Later\n";

        var item = BacklogParser.Parse(Config(), markdown).Single(i => i.Code == "PROJ-101");

        var result = BacklogSplicer.ReplaceDescription(markdown, item, "new");

        Assert.Equal("## Epic 1 — First\n### PROJ-101 · Issue\nnew\n\n\n### PROJ-102 · Later\n", result);
    }

    [Fact]
    public void AFileWithoutATrailingNewlineStaysWithoutOne()
    {
        const string markdown = "## Epic 1 — First\n### PROJ-101 · Issue\nold";

        var item = BacklogParser.Parse(Config(), markdown).Single(i => i.Code == "PROJ-101");

        var result = BacklogSplicer.ReplaceDescription(markdown, item, "new\n- a task");

        Assert.Equal("## Epic 1 — First\n### PROJ-101 · Issue\nnew\n- a task", result);
    }

    [Fact]
    public void SplicingSeveralEditsLastToFirstKeepsTheLaterRangesValid()
    {
        const string markdown = """
            ## Epic 1 — First

            ### PROJ-101 · A
            first old

            ### PROJ-102 · B
            second old

            """;

        var items = BacklogParser.Parse(Config(), markdown);

        // This is the order SaveAsync uses: a splice changes line counts, so an
        // earlier splice would invalidate the ranges of everything after it.
        var result = markdown;
        foreach (var item in items.Where(i => i.Code is not null).Reverse())
        {
            result = BacklogSplicer.ReplaceDescription(result, item, $"new for {item.Code}");
        }

        Assert.Equal(
            """
            ## Epic 1 — First

            ### PROJ-101 · A
            new for PROJ-101

            ### PROJ-102 · B
            new for PROJ-102

            """,
            result);
    }

    [Fact]
    public void SplicingAnEditedBulletReparsesToTheNewTasks()
    {
        const string markdown = "## Epic 1 — First\n### PROJ-101 · Issue\n- old task\nnote\n";

        var item = BacklogParser.Parse(Config(), markdown).Single(i => i.Code == "PROJ-101");

        var result = BacklogSplicer.ReplaceDescription(markdown, item, "note\n- new task\n- another");

        var reparsed = BacklogParser.Parse(Config(), result).Single(i => i.Code == "PROJ-101");
        Assert.Equal(["new task", "another"], reparsed.Bullets);
    }

    [Fact]
    public void SplicingEveryItemBackWithItsOwnLinesReproducesTheFile()
    {
        var markdown = File.ReadAllText(RepoPaths.Fixture("backlog", "standard.md"));
        var items = BacklogParser.Parse(Config(), markdown);

        // Any order works when the replacement is byte-identical: line counts
        // never change, so every range stays valid throughout.
        var result = items.Aggregate(
            markdown,
            (current, item) => BacklogSplicer.ReplaceDescription(
                current, item, string.Join("\n", item.DescriptionLines)));

        Assert.Equal(markdown, result);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(2, 1)]
    [InlineData(0, 99)]
    public void ARangeOutsideTheTextIsRefused(int start, int end)
    {
        const string markdown = "## Epic 1 — First\n### PROJ-101 · Issue\nold\n";
        var item = BacklogParser.Parse(Config(), markdown).Single(i => i.Code == "PROJ-101");
        var outside = item with { DescriptionStart = start, DescriptionEnd = end };

        Assert.Throws<ArgumentOutOfRangeException>(
            () => BacklogSplicer.ReplaceDescription(markdown, outside, "new"));
    }
}
