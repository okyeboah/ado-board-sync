namespace AdoBoardSync.Infrastructure;

/// <summary>
/// Where this machine's own data lives: the profile registry, the operation
/// history, the diagnostics log. Never inside a repository or a profile's working
/// directory — these files name every board a person works on and hold the prompts
/// they have run, so they must not be somewhere a user commits or ships by accident.
///
/// The environment override exists because the alternative is worse than the
/// indirection. Three adapters each resolved this root for themselves, which meant
/// a test that opened the real shell wrote its fixture profiles into the developer's
/// own registry and its runs into their own history. A test suite that edits the
/// user's data is a test suite people stop running.
/// </summary>
public static class LocalDataPaths
{
    /// <summary>
    /// Overrides the root. Set by the test harness; also the seam a portable
    /// install would use to keep its data beside itself rather than in the profile.
    /// </summary>
    public const string OverrideVariable = "ADO_BOARD_SYNC_DATA_DIR";

    /// <summary>
    /// The root for this machine's data. Read once: the value is baked into static
    /// paths on first touch, so a later change would apply to some callers and not
    /// others — which is harder to diagnose than not honouring it at all.
    /// </summary>
    public static string Root { get; } = ResolveRoot();

    /// <summary>One directory under the root, created lazily by whoever writes to it.</summary>
    public static string Directory(string name) => Path.Combine(Root, name);

    private static string ResolveRoot()
    {
        if (Environment.GetEnvironmentVariable(OverrideVariable) is { Length: > 0 } overridden)
        {
            return overridden;
        }

        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        if (string.IsNullOrEmpty(root))
        {
            root = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        // A container, or a service account with no profile at all. The data still
        // has to land somewhere, and a temporary directory loses it on reboot rather
        // than failing to start.
        if (string.IsNullOrEmpty(root))
        {
            root = Path.GetTempPath();
        }

        return root;
    }
}
