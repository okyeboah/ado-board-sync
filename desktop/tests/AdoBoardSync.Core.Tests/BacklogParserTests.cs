using AdoBoardSync.Core.Backlog;
using AdoBoardSync.Core.Configuration;
using AdoBoardSync.TestKit;

namespace AdoBoardSync.Core.Tests;

/// <summary>
/// Pins the parsing rules the editor depends on. The parity suite proves the .NET
/// parser agrees with the CLI; these tests state what that agreed behaviour is, so
/// a deliberate change to both is still a visible decision rather than a silent one.
/// </summary>
public class BacklogParserTests
{
    private static BoardConfig Config(string? extraJson = null)
    {
        var json = extraJson is null
            ? """{"org": "o", "project": "p", "code_prefix": "PROJ"}"""
            : $$"""{"org": "o", "project": "p", "code_prefix": "PROJ", {{extraJson}}}""";

        var result = BoardConfig.Parse(json, Path.GetTempPath());
        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        return result.Value;
    }

    [Fact]
    public void ContentBeforeTheFirstEpicHeadingIsIgnored()
    {
        var items = BacklogParser.Parse(Config(), """
            # Title
            Prose that belongs to nobody.
            ### PROJ-100 · An issue before any epic

            ## Epic 1 — First
            ### PROJ-101 · Inside the epic
            """);

        Assert.Collection(
            items,
            item => Assert.Equal(BacklogLevel.Epic, item.Level),
            item => Assert.Equal("PROJ-101", item.Code));
    }

    [Fact]
    public void ParsingStopsAtTheFirstStopHeading()
    {
        var items = BacklogParser.Parse(
            Config("""
                "stop_headings": ["## Appendix"]
                """),
            """
            ## Epic 1 — First
            ### PROJ-101 · Kept

            ## Appendix — Deferred
            ### PROJ-999 · Dropped
            """);

        Assert.Equal(["PROJ-101"], items.Where(i => i.Code is not null).Select(i => i.Code));
    }

    [Fact]
    public void OnlyDashBulletsBecomeTasks()
    {
        var items = BacklogParser.Parse(Config(), """
            ## Epic 1 — First
            ### PROJ-101 · Issue
            - a dash bullet
            * an asterisk bullet
            + a plus bullet
            """);

        var issue = items.Single(i => i.Level == BacklogLevel.Issue);
        Assert.Equal(["a dash bullet"], issue.Bullets);
    }

    [Fact]
    public void AnIndentedDashBulletAlsoBecomesATask()
    {
        // The CLI checks the stripped line, so indentation does not exclude a dash
        // bullet. The README's "top-level bullets" wording holds only because nested
        // bullets conventionally use '*'. Documented here because it surprises people.
        var items = BacklogParser.Parse(Config(), """
            ## Epic 1 — First
            ### PROJ-101 · Issue
            - top level
              - indented dash
            """);

        var issue = items.Single(i => i.Level == BacklogLevel.Issue);
        Assert.Equal(["top level", "indented dash"], issue.Bullets);
    }

    [Fact]
    public void CodeMatchingIsCaseSensitiveAgainstTheConfiguredPrefix()
    {
        // The code regex is built from code_prefix verbatim, so with prefix "PROJ" a
        // "### proj-101" heading is not an Issue at all — its bullets silently fold
        // into the previous Issue's description. Worth pinning: the failure is quiet.
        var items = BacklogParser.Parse(Config(), """
            ## Epic 1 — First
            ### proj-101 · Lowercase heading, uppercase prefix
            - a task
            """);

        Assert.DoesNotContain(items, i => i.Level == BacklogLevel.Issue);
    }

    [Fact]
    public void AMatchedCodeIsUpperCased()
    {
        // .upper() only changes anything when code_prefix is itself lowercase.
        var lowercasePrefix = BoardConfig.Parse(
            """{"org": "o", "project": "p", "code_prefix": "proj"}""",
            Path.GetTempPath());
        Assert.True(lowercasePrefix.IsSuccess, lowercasePrefix.Error?.SafeMessage);

        var items = BacklogParser.Parse(
            lowercasePrefix.Value,
            """
            ## Epic 1 — First
            ### proj-101 · Lowercase heading, lowercase prefix
            """);

        Assert.Equal("PROJ-101", items.Single(i => i.Level == BacklogLevel.Issue).Code);
    }

    [Fact]
    public void AHeadingWithoutACodeIsNotAnIssue()
    {
        var items = BacklogParser.Parse(Config(), """
            ## Epic 1 — First
            ### A heading with no code
            ### PROJ-101 · A real issue
            """);

        Assert.Single(items, i => i.Level == BacklogLevel.Issue);
    }

    [Fact]
    public void LeadingBlankLinesAreDroppedFromADescription()
    {
        var items = BacklogParser.Parse(Config(), "## Epic 1 — First\n\n\nFirst real line.\n");

        Assert.Equal(["First real line."], items.Single().DescriptionLines);
    }

    [Fact]
    public void TheEpicTitleComesFromTheConfiguredRegexGroup()
    {
        var items = BacklogParser.Parse(Config(), "## Epic 1 — Platform Foundations\n");

        Assert.Equal("Epic 1 — Platform Foundations", items.Single().Title);
    }

    [Fact]
    public void ACustomEpicRegexIsHonoured()
    {
        var items = BacklogParser.Parse(
            Config("""
                "epic_heading_regex": "^##\\s+(Theme\\b.*)$"
                """),
            """
            ## Theme A — Custom heading
            ### PROJ-101 · Issue
            """);

        Assert.Equal("Theme A — Custom heading", items[0].Title);
    }

    [Theory]
    [InlineData("a\r\nb\r\n")]
    [InlineData("a\nb\n")]
    [InlineData("a\rb\r")]
    public void LineEndingsAreNormalisedBeforeParsing(string body)
    {
        var items = BacklogParser.Parse(Config(), "## Epic 1 — First\n" + body);

        Assert.Equal(["a", "b"], items.Single().DescriptionLines);
    }

    [Theory]
    [InlineData(0x0C, "form feed")]
    [InlineData(0x0B, "vertical tab")]
    [InlineData(0x85, "next line")]
    [InlineData(0x2028, "line separator")]
    [InlineData(0x2029, "paragraph separator")]
    public void OnlyCarriageReturnAndLineFeedEndALine(int codePoint, string description)
    {
        // Python's text mode splits on CR, LF, and CRLF only. string.ReplaceLineEndings
        // and string.Split would also break on these five, which would silently give the
        // desktop app a different line count than the CLI for the same file.
        var body = "a" + (char)codePoint + "b";

        var lines = BacklogParser
            .Parse(Config(), "## Epic 1 — First\n" + body + "\n")
            .Single()
            .DescriptionLines;

        Assert.True(
            lines.Count == 1,
            $"{description} (U+{codePoint:X4}) must not end a line, but produced {lines.Count}");
        Assert.Equal(body, lines[0]);
    }

    [Fact]
    public void AnEmptyDocumentYieldsNoItems() =>
        Assert.Empty(BacklogParser.Parse(Config(), string.Empty));

    [Fact]
    public void TasksByCodeCoversEveryIssueIncludingOnesWithNoBullets()
    {
        var tasks = BacklogParser.TasksByCode(Config(), """
            ## Epic 1 — First
            ### PROJ-101 · Has bullets
            - one
            ### PROJ-102 · Has none
            """);

        Assert.Equal(["PROJ-101", "PROJ-102"], tasks.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Empty(tasks["PROJ-102"]);
    }

    [Fact]
    public void EveryItemCarriesTheRangeOfItsDescriptionBlock()
    {
        // Line indices into the split text: the block starts at the first
        // description line (leading blanks belong to the gap) and ends where the
        // next heading begins. BacklogSplicer saves through these ranges, so the
        // editor's splice can never disagree with the parse about what is inside.
        var items = BacklogParser.Parse(Config(), """
            ## Epic 1 — First
            ### PROJ-101 · Issue
            - a task
            desc line

            ### PROJ-102 · Second
            """);

        Assert.Collection(
            items,
            epic =>
            {
                Assert.Equal(BacklogLevel.Epic, epic.Level);
                Assert.Equal(1, epic.DescriptionStart);
                Assert.Equal(1, epic.DescriptionEnd);
            },
            first =>
            {
                Assert.Equal(2, first.DescriptionStart);
                Assert.Equal(5, first.DescriptionEnd);
            },
            last =>
            {
                Assert.Equal(6, last.DescriptionStart);
                Assert.Equal(6, last.DescriptionEnd);
            });
    }

    [Fact]
    public void LeadingBlanksBelongToTheGapNotTheBlock()
    {
        var items = BacklogParser.Parse(Config(), "## Epic 1 — First\n\n\nFirst real line.\n");

        var item = items.Single();
        Assert.Equal(3, item.DescriptionStart);
        Assert.Equal(4, item.DescriptionEnd);
    }

    [Fact]
    public void TheDescriptionRangeCoversExactlyTheDescriptionLines()
    {
        var markdown = File.ReadAllText(RepoPaths.Fixture("backlog", "standard.md"));
        var lines = Core.PythonCompat.SplitLines(markdown).ToList();

        foreach (var item in BacklogParser.Parse(Config(), markdown))
        {
            Assert.Equal(
                item.DescriptionLines,
                lines.Skip(item.DescriptionStart).Take(item.DescriptionEnd - item.DescriptionStart).ToList());
        }
    }
}
