using AdoBoardSync.Core.Backlog;
using AdoBoardSync.Core.Configuration;
using AdoBoardSync.Core.Csv;

namespace AdoBoardSync.Core.Tests;

/// <summary>
/// States the CSV export's behaviour directly, so the quoting rules are visible
/// without reading the parity driver: minimal quoting, doubled quotes, CRLF
/// records, Epics in Title 1 and Issues in Title 2.
/// </summary>
public class ImportCsvTests
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

    private static BacklogItem Epic(string title, params string[] description) => new()
    {
        Level = BacklogLevel.Epic,
        Title = title,
        DescriptionLines = description,
    };

    private static BacklogItem Issue(string code, string title, params string[] description) => new()
    {
        Level = BacklogLevel.Issue,
        Title = title,
        Code = code,
        DescriptionLines = description,
        Bullets = BacklogParser.BulletsOf(BacklogLevel.Issue, description),
    };

    [Fact]
    public void TheHeaderRowIsAlwaysFirst()
    {
        var csv = ImportCsv.Serialize(Config(), []);

        Assert.Equal("Work Item Type,Title 1,Title 2,Description\r\n", csv);
    }

    [Fact]
    public void AnEpicTitleGoesInTitle1AndAnIssueTitleInTitle2()
    {
        var csv = ImportCsv.Serialize(Config(), [Epic("Epic 1 — First"), Issue("PROJ-101", "PROJ-101 · Do")]);

        Assert.Equal(
            "Work Item Type,Title 1,Title 2,Description\r\n" +
            "Epic,Epic 1 — First,,\r\n" +
            "Issue,,PROJ-101 · Do,\r\n",
            csv);
    }

    [Fact]
    public void TheDescriptionIsTheConvertedHtml()
    {
        var csv = ImportCsv.Serialize(Config(), [Issue("PROJ-101", "PROJ-101 · Do", "Some **bold** text")]);

        // Minimal quoting: no comma, quote, or newline in the field, no quotes.
        Assert.Contains("Issue,,PROJ-101 · Do,<p>Some <b>bold</b> text</p>\r\n", csv);
    }

    [Fact]
    public void ABulletListKeepsTheConverterNewlinesInsideTheQuotedField()
    {
        var csv = ImportCsv.Serialize(Config(), [Issue("PROJ-101", "PROJ-101 · Do", "- a task")]);

        Assert.Contains("\"<ul>\n<li>a task</li>\n</ul>\"", csv);
    }

    [Fact]
    public void AFieldWithACommaIsQuoted()
    {
        var csv = ImportCsv.Serialize(Config(), [Epic("Epic, with comma")]);

        Assert.Contains("\"Epic, with comma\"", csv);
    }

    [Fact]
    public void AQuoteInsideAFieldIsDoubledAndTheFieldIsQuoted()
    {
        var csv = ImportCsv.Serialize(Config(), [Issue("PROJ-101", "PROJ-101 · Say \"hello\"")]);

        Assert.Contains("\"PROJ-101 · Say \"\"hello\"\"\"", csv);
    }

    [Fact]
    public void AMultiLineDescriptionIsQuotedWithEmbeddedNewlines()
    {
        var csv = ImportCsv.Serialize(
            Config(),
            [Issue("PROJ-101", "PROJ-101 · Do", "first block", "", "second block")]);

        // One record, one terminating CRLF. The description's internal newline is
        // the converter's LF; the field is quoted because it contains it.
        Assert.Equal(
            "Work Item Type,Title 1,Title 2,Description\r\n" +
            "Issue,,PROJ-101 · Do,\"<p>first block</p>\n<p>second block</p>\"\r\n",
            csv);
    }

    [Fact]
    public void TheConfiguredTypeNamesAreUsed()
    {
        var csv = ImportCsv.Serialize(
            Config("""
                "types": {"epic": "Feature", "story": "User Story"}
                """),
            [Epic("Epic 1 — First"), Issue("PROJ-101", "PROJ-101 · Do")]);

        Assert.Contains("Feature,Epic 1 — First,,\r\n", csv);
        Assert.Contains("User Story,,PROJ-101 · Do,\r\n", csv);
    }
}
