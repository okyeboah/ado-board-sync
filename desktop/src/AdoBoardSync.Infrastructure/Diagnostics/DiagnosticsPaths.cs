namespace AdoBoardSync.Infrastructure.Diagnostics;

/// <summary>
/// Where diagnostics live on disk (ABSD-507). One place, because the sink writes
/// these files and <see cref="DiagnosticsBundle"/> reads them — a name agreed in
/// two places is a bundle that quietly ships no logs.
/// </summary>
public static class DiagnosticsPaths
{
    public const string LogFileName = "diagnostics.jsonl";

    /// <summary>Matches the current file and every rotated archive beside it.</summary>
    public const string LogFileSearchPattern = "diagnostics*.jsonl";

    /// <summary>
    /// Under the user's local application data rather than beside the backlog: a
    /// backlog lives in a git repository, and a log that follows it there is a log
    /// that eventually gets committed.
    /// </summary>
    public static string DefaultDirectory { get; } = BuildDefaultDirectory();

    /// <summary>
    /// The name a rotated file takes. Numbered rather than timestamped so the sink
    /// can find the file to delete without parsing dates, and so a support
    /// conversation can say "the file ending .1" and be understood.
    /// </summary>
    public static string ArchiveFileName(int index) => $"diagnostics.{index}.jsonl";

    private static string BuildDefaultDirectory()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        // Empty on a machine where the folder is not configured — a container, or a
        // service account with no profile. Diagnostics still have to land somewhere,
        // and a temp directory is a better answer than a path rooted at "".
        if (string.IsNullOrEmpty(root))
        {
            root = Path.GetTempPath();
        }

        return Path.Combine(root, "AdoBoardSync", "logs");
    }
}
