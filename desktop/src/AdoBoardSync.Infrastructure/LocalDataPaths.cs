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

    /// <summary>
    /// The one directory this application keeps its data in.
    ///
    /// One name, not two. The registry and the log used "AdoBoardSync" while the
    /// history used "ado-board-sync", so the app kept its things in two folders
    /// side by side — which a user looking for them finds confusing, and an
    /// uninstaller removing one leaves half behind.
    /// </summary>
    public const string DirectoryName = "AdoBoardSync";

    /// <summary>The name the operation history used before the two were unified.</summary>
    internal const string LegacyHistoryDirectoryName = "ado-board-sync";

    /// <summary>One directory under the root, created lazily by whoever writes to it.</summary>
    public static string Directory(string name) => Path.Combine(Root, name);

    /// <summary>This application's own directory under the root.</summary>
    public static string Own() => Directory(DirectoryName);

    /// <summary>
    /// Moves a file left behind under the old directory name into the new one, and
    /// answers where it ended up.
    ///
    /// Deliberately conservative. It moves only when there is nothing at the new
    /// path to lose, and it never deletes: a failed move leaves the old file exactly
    /// where it was and the caller simply keeps using it. Losing a user's operation
    /// history to a tidy-up would be a far worse outcome than two directories.
    /// </summary>
    internal static string Adopted(string fileName)
    {
        var current = Path.Combine(Own(), fileName);
        var legacy = Path.Combine(Directory(LegacyHistoryDirectoryName), fileName);

        if (File.Exists(current) || !File.Exists(legacy))
        {
            return current;
        }

        try
        {
            System.IO.Directory.CreateDirectory(Own());
            File.Move(legacy, current);
            return current;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Keep reading the old file rather than starting an empty new one.
            return legacy;
        }
    }

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
