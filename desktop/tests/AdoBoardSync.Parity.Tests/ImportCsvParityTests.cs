using System.Text.Json;
using AdoBoardSync.Core.Backlog;
using AdoBoardSync.Core.Configuration;
using AdoBoardSync.Core.Csv;
using AdoBoardSync.TestKit;

namespace AdoBoardSync.Parity.Tests;

/// <summary>
/// Proves the .NET CSV export writes exactly what the CLI's <c>gen-csv</c> writes:
/// same columns, same quoting, same CRLF records. The file's whole purpose is to be
/// interchangeable — the Azure DevOps web importer consumes it, and a diff against
/// the CLI's output is how a user verifies that.
/// </summary>
public class ImportCsvParityTests
{
    public static TheoryData<string> BacklogFixtures => Fixtures.BacklogFiles;

    [Theory]
    [MemberData(nameof(BacklogFixtures))]
    public void Csv_MatchesThePythonImplementation(string fixture)
    {
        var backlog = RepoPaths.Fixture(Fixtures.Backlog, fixture);
        using var profile = TempBoardProfile.Create(backlog);

        using var reference = PythonReference.WithConfig("csv", profile.ConfigPath);
        var config = BoardConfig.Load(profile.ConfigPath);
        Assert.True(config.IsSuccess, config.Error?.SafeMessage);

        var items = BacklogParser.Parse(config.Value, File.ReadAllText(backlog));
        var actual = ImportCsv.Serialize(config.Value, items);

        Assert.Equal(
            reference.RootElement.GetProperty("value").GetString(),
            actual);
    }
}
