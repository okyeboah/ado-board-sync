using AdoBoardSync.Core.Configuration;
using AdoBoardSync.Core.Results;

namespace AdoBoardSync.Core.Tests;

/// <summary>
/// Covers credential resolution order. The CLI's contract is environment variable
/// first, then the gitignored token file, so a project already set up for the CLI
/// keeps working when opened in the desktop app without touching anything.
/// </summary>
public class PatResolverTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("abs-pat-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void TheEnvironmentVariableWinsOverTheTokenFile()
    {
        var config = Config();
        WriteTokenFile("from-file");

        using var environment = new ScopedEnvironmentVariable(config.PatEnv, "  from-env  ");

        Assert.Equal("from-env", PatResolver.ForConfig(config).Resolve());
    }

    [Fact]
    public void TheTokenFileIsUsedWhenTheEnvironmentVariableIsUnset()
    {
        var config = Config();
        WriteTokenFile("from-file\n");

        using var environment = new ScopedEnvironmentVariable(config.PatEnv, null);

        Assert.Equal("from-file", PatResolver.ForConfig(config).Resolve());
    }

    [Fact]
    public void NoTokenAnywhereResolvesToNull()
    {
        var config = Config();

        using var environment = new ScopedEnvironmentVariable(config.PatEnv, null);

        Assert.Null(PatResolver.ForConfig(config).Resolve());
    }

    [Fact]
    public void AWhitespaceOnlyTokenFileCountsAsAbsent()
    {
        var config = Config();
        WriteTokenFile("   \n");

        using var environment = new ScopedEnvironmentVariable(config.PatEnv, null);

        Assert.Null(PatResolver.ForConfig(config).Resolve());
    }

    [Fact]
    public void AWhitespaceOnlyEnvironmentVariableFallsThroughToTheFile()
    {
        var config = Config();
        WriteTokenFile("from-file");

        using var environment = new ScopedEnvironmentVariable(config.PatEnv, "   ");

        Assert.Equal("from-file", PatResolver.ForConfig(config).Resolve());
    }

    [Fact]
    public void SourcesAreTriedInOrderAndTheFirstHitWins()
    {
        var resolver = new PatResolver([
            new StubSource("empty", null),
            new StubSource("first-hit", "a"),
            new StubSource("second-hit", "b")
        ]);

        Assert.Equal("a", resolver.Resolve());
    }

    [Fact]
    public void DescribeSourcesNamesEverySourceAndLeaksNoToken()
    {
        var config = Config();
        WriteTokenFile("super-secret-token");

        var description = PatResolver.ForConfig(config).DescribeSources();

        Assert.Contains(config.PatEnv, description, StringComparison.Ordinal);
        Assert.Contains(config.PatFile, description, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret-token", description, StringComparison.Ordinal);
    }

    private void WriteTokenFile(string contents) =>
        File.WriteAllText(Path.Combine(_directory, ".ado_pat"), contents);

    private BoardConfig Config()
    {
        var result = BoardConfig.Parse(
            """{"org": "demo-org", "project": "DemoProject", "code_prefix": "PROJ"}""",
            _directory);

        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        return result.Value;
    }

    private sealed class StubSource(string name, string? token) : IPatSource
    {
        public string Name => name;

        public Result<string?> TryRead() => token;
    }

    private sealed class ScopedEnvironmentVariable : IDisposable
    {
        private readonly string _name;
        private readonly string? _original;

        public ScopedEnvironmentVariable(string name, string? value)
        {
            _name = name;
            _original = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _original);
    }
}
