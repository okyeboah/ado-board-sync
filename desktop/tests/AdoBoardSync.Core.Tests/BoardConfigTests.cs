using AdoBoardSync.Core.Configuration;
using AdoBoardSync.Core.Results;

namespace AdoBoardSync.Core.Tests;

/// <summary>
/// Covers the config paths the parity suite cannot reach: the CLI exits the
/// process on a bad config, so there is no Python behaviour to compare a typed
/// failure against. The desktop app must surface these as errors a window can
/// show, never as a crash.
/// </summary>
public class BoardConfigTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"abs-config-{Guid.NewGuid():N}");

    public BoardConfigTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Load_ReportsNotFound_WhenTheConfigIsMissing()
    {
        var result = BoardConfig.Load(Path.Combine(_directory, "absent.json"));

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.NotFound, result.Error!.Kind);
    }

    [Theory]
    [InlineData("""{"project": "P", "code_prefix": "X"}""", "org")]
    [InlineData("""{"org": "O", "code_prefix": "X"}""", "project")]
    [InlineData("""{"org": "O", "project": "P"}""", "code_prefix")]
    [InlineData("""{"org": "", "project": "P", "code_prefix": "X"}""", "org")]
    public void Parse_NamesEveryMissingRequiredKey(string json, string expectedKey)
    {
        var result = BoardConfig.Parse(json, _directory);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.Validation, result.Error!.Kind);
        Assert.Contains(expectedKey, result.Error.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ListsEveryMissingKeyAtOnce()
    {
        var result = BoardConfig.Parse("{}", _directory);

        Assert.True(result.IsFailure);
        Assert.Contains("org", result.Error!.SafeMessage, StringComparison.Ordinal);
        Assert.Contains("project", result.Error.SafeMessage, StringComparison.Ordinal);
        Assert.Contains("code_prefix", result.Error.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ReportsInvalidJsonRatherThanThrowing()
    {
        var result = BoardConfig.Parse("{ not json", _directory);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.Validation, result.Error!.Kind);
    }

    [Fact]
    public void Parse_ReportsAnInvalidEpicHeadingRegex()
    {
        var result = BoardConfig.Parse(
            """{"org": "O", "project": "P", "code_prefix": "X", "epic_heading_regex": "(unclosed"}""",
            _directory);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.Validation, result.Error!.Kind);
    }

    [Fact]
    public void RelativePaths_ResolveAgainstTheConfigDirectory()
    {
        var config = Valid();

        Assert.Equal(Path.Combine(_directory, "docs", "backlog.md"), config.BoardFile);
        Assert.Equal(Path.Combine(_directory, "build", "work-items.csv"), config.CsvFile);
    }

    [Fact]
    public void AbsolutePaths_AreLeftAlone()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "elsewhere", "backlog.md");
        var config = Valid($$"""{"org":"O","project":"P","code_prefix":"X","board_file":{{System.Text.Json.JsonSerializer.Serialize(absolute)}}}""");

        Assert.Equal(absolute, config.BoardFile);
    }

    [Fact]
    public void ResolvePath_ResolvesAgainstTheConfigDirectory() =>
        Assert.Equal(Path.Combine(_directory, ".ado_pat"), Valid().ResolvePath(".ado_pat"));

    private BoardConfig Valid(string? json = null)
    {
        var result = BoardConfig.Parse(
            json ?? """{"org": "demo-org", "project": "DemoProject", "code_prefix": "PROJ"}""",
            _directory);

        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        return result.Value;
    }
}
