using System.Text.Json;
using AdoBoardSync.Core.Configuration;
using AdoBoardSync.Core.Results;
using AdoBoardSync.TestKit;

namespace AdoBoardSync.Core.Tests;

/// <summary>
/// Covers the schema constraints that a plain deserialize does not enforce.
///
/// The first test is the important one: it reads the real
/// <c>board.config.schema.json</c>, so a key added to the schema without being
/// added to the validator fails the build rather than being silently accepted.
/// </summary>
public class BoardConfigSchemaTests
{
    private const string Minimal = """{"org": "o", "project": "p", "code_prefix": "PROJ"}""";

    [Fact]
    public void EveryPropertyInTheSchemaFileIsKnownHere()
    {
        using var schema = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(RepoPaths.Root, "board.config.schema.json")));

        var inSchema = schema.RootElement
            .GetProperty("properties")
            .EnumerateObject()
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(inSchema, BoardConfigSchema.KnownProperties.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void TheSchemaStillForbidsAdditionalProperties()
    {
        // The unknown-key check below is only correct while the schema says so.
        using var schema = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(RepoPaths.Root, "board.config.schema.json")));

        Assert.False(schema.RootElement.GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public void AValidConfigPasses() => Assert.Null(BoardConfigSchema.Validate(Minimal));

    [Fact]
    public void AMistypedKeyIsRejectedAndTheRealKeyIsSuggested()
    {
        var error = BoardConfigSchema.Validate(
            """{"org": "o", "project": "p", "code_prefix": "PROJ", "code_prefixx": "X"}""");

        Assert.NotNull(error);
        Assert.Equal("config.unknown_key", error.Code);
        Assert.Contains("'code_prefix'", error.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnrecognisableKeyIsRejectedWithoutAMisleadingSuggestion()
    {
        var error = BoardConfigSchema.Validate(
            """{"org": "o", "project": "p", "code_prefix": "PROJ", "zzzzzzzzzzzz": 1}""");

        Assert.NotNull(error);
        Assert.Equal("config.unknown_key", error.Code);
        Assert.Contains("board.config.schema.json", error.SafeMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1PROJ")]
    [InlineData("PRO-J")]
    [InlineData("PRO J")]
    [InlineData("")]
    public void AnInvalidCodePrefixIsRejected(string prefix)
    {
        var error = BoardConfigSchema.Validate(
            $$"""{"org": "o", "project": "p", "code_prefix": {{JsonSerializer.Serialize(prefix)}}}""");

        Assert.NotNull(error);
        Assert.Equal("config.bad_code_prefix", error.Code);
    }

    [Theory]
    [InlineData("PROJ")]
    [InlineData("P")]
    [InlineData("Proj2")]
    public void AValidCodePrefixIsAccepted(string prefix) =>
        Assert.Null(BoardConfigSchema.Validate(
            $$"""{"org": "o", "project": "p", "code_prefix": {{JsonSerializer.Serialize(prefix)}}}"""));

    [Theory]
    [InlineData("task_title_max", 0)]
    [InlineData("max_retries", -1)]
    [InlineData("backoff", -0.5)]
    [InlineData("timeout", 0)]
    public void AValueBelowItsMinimumIsRejected(string key, double value)
    {
        var error = BoardConfigSchema.Validate(
            $$"""{"org": "o", "project": "p", "code_prefix": "PROJ", "{{key}}": {{value}}}""");

        Assert.NotNull(error);
        Assert.Equal("config.below_minimum", error.Code);
        Assert.Contains(key, error.SafeMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("task_title_max", 1)]
    [InlineData("max_retries", 0)]
    [InlineData("backoff", 0)]
    [InlineData("timeout", 1)]
    public void AValueExactlyAtItsMinimumIsAccepted(string key, double value) =>
        Assert.Null(BoardConfigSchema.Validate(
            $$"""{"org": "o", "project": "p", "code_prefix": "PROJ", "{{key}}": {{value}}}"""));

    [Fact]
    public void AnIterationWithoutANameIsRejected()
    {
        var error = BoardConfigSchema.Validate(
            """{"org":"o","project":"p","code_prefix":"PROJ","iterations":[{"start":"2026-01-01"}]}""");

        Assert.NotNull(error);
        Assert.Equal("config.iteration_without_name", error.Code);
        Assert.Contains("iterations[0]", error.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOffendingIterationIndexIsReported()
    {
        var error = BoardConfigSchema.Validate(
            """{"org":"o","project":"p","code_prefix":"PROJ","iterations":[{"name":"S1"},{"name":"  "}]}""");

        Assert.NotNull(error);
        Assert.Contains("iterations[1]", error.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ANonObjectDocumentIsRejected()
    {
        var error = BoardConfigSchema.Validate("[]");

        Assert.NotNull(error);
        Assert.Equal("config.not_an_object", error.Code);
    }

    [Fact]
    public void BoardConfigParseAppliesTheSchemaCheck()
    {
        var result = BoardConfig.Parse(
            """{"org": "o", "project": "p", "code_prefix": "PROJ", "timeuot": 5}""",
            Path.GetTempPath());

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.Validation, result.Error!.Kind);
        Assert.Equal("config.unknown_key", result.Error.Code);
    }

    [Fact]
    public void TheShippedExampleConfigSatisfiesTheSchema() =>
        Assert.Null(BoardConfigSchema.Validate(
            File.ReadAllText(Path.Combine(RepoPaths.Root, "board.config.example.json"))));
}
