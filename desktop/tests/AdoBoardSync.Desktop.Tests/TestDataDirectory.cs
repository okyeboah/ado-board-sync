using System.Runtime.CompilerServices;
using AdoBoardSync.Infrastructure;

namespace AdoBoardSync.Desktop.Tests;

/// <summary>
/// Points this machine's data — the profile registry, the operation history, the
/// diagnostics log — at a directory belonging to the test run.
///
/// This is not tidiness. Opening the real shell adopts a profile, and adopting a
/// profile registers it, so without this a test that renders the window writes its
/// fixture paths into the developer's own registry and its runs into their own
/// history. The module initialiser is what makes it reliable: every one of those
/// paths is a static resolved on first touch, so the variable has to be set before
/// any test body runs, not in a fixture that races them.
/// </summary>
internal static class TestDataDirectory
{
    [ModuleInitializer]
    internal static void Redirect()
    {
        // Per process, not per test: the paths are static, so one directory for the
        // run is the most isolation that is actually achievable — and it is enough,
        // because the point is to stay out of the user's own data.
        var directory = Path.Combine(
            Path.GetTempPath(), $"absd-tests-{Environment.ProcessId}-{Guid.NewGuid():N}");

        Environment.SetEnvironmentVariable(LocalDataPaths.OverrideVariable, directory);
    }
}
