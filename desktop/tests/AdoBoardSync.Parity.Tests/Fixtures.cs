using AdoBoardSync.TestKit;

namespace AdoBoardSync.Parity.Tests;

/// <summary>
/// The fixture sets the parity theories run over.
///
/// Declared once rather than per test class: a fixture added to a directory must
/// reach every comparison that directory feeds, and duplicated members drift.
/// </summary>
public static class Fixtures
{
    public const string Markup = "markup";
    public const string Backlog = "backlog";

    public static TheoryData<string> MarkupFiles => Named(Markup);

    public static TheoryData<string> BacklogFiles => Named(Backlog);

    /// <summary>Both directories as (directory, file) pairs, for theories that span them.</summary>
    public static TheoryData<string, string> AllFiles
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var directory in new[] { Markup, Backlog })
            {
                foreach (var name in RepoPaths.FixtureNames(directory))
                {
                    data.Add(directory, name);
                }
            }

            return data;
        }
    }

    private static TheoryData<string> Named(string subdirectory) =>
        [.. RepoPaths.FixtureNames(subdirectory)];
}
