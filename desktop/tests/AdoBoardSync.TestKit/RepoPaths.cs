namespace AdoBoardSync.TestKit;

/// <summary>
/// Locates repository files from a test binary.
///
/// Fixtures are read from the repository rather than copied to the output
/// directory: the parity suite feeds the same fixture to the Python CLI and to
/// the .NET port, and a stale copy in bin/ would let the two drift apart while
/// still reporting a pass.
/// </summary>
public static class RepoPaths
{
    private static readonly Lazy<string> RootValue = new(FindRoot);

    public static string Root => RootValue.Value;

    public static string ParityDriver =>
        Path.Combine(Root, "desktop", "tests", "parity", "parity_driver.py");

    public static string Fixtures =>
        Path.Combine(Root, "desktop", "tests", "fixtures");

    public static string Fixture(params string[] segments) =>
        Path.Combine([Fixtures, .. segments]);

    /// <summary>
    /// The fixture file names in a subdirectory, ordered so a theory's cases are
    /// stable between runs and between machines.
    /// </summary>
    public static IReadOnlyList<string> FixtureNames(string subdirectory) =>
        [.. Directory.EnumerateFiles(Path.Combine(Fixtures, subdirectory), "*.md")
            .Select(Path.GetFileName)
            .OfType<string>()
            .OrderBy(name => name, StringComparer.Ordinal)];

    /// <summary>
    /// Names the repository root explicitly, for a build whose output does not sit
    /// under it. <c>dotnet build --artifacts-path</c> is the case that matters:
    /// it is how two agents build the same checkout without sharing obj/, and
    /// without this every fixture-reading test fails on a path walk that can no
    /// longer reach the root.
    /// </summary>
    public const string RootVariable = "ADO_BOARD_SYNC_REPO_ROOT";

    private static string FindRoot()
    {
        if (Environment.GetEnvironmentVariable(RootVariable) is { Length: > 0 } named)
        {
            var full = Path.GetFullPath(named);
            if (!File.Exists(Path.Combine(full, "pyproject.toml")))
            {
                throw new InvalidOperationException(
                    $"{RootVariable} is set to {full}, which is not the repository root: no pyproject.toml there.");
            }

            return full;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "pyproject.toml")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not find the repository root above {AppContext.BaseDirectory}: no pyproject.toml found. "
            + $"Set {RootVariable} when building out of tree.");
    }
}
