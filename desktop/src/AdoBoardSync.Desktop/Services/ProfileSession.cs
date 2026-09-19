using AdoBoardSync.Core.Results;
using AdoBoardSync.Desktop.Services;

namespace AdoBoardSync.Desktop.Services;

/// <summary>
///     The profile's life between the file and the shell: open, re-open, reload,
///     and the staleness poll. Extracted from the shell view model because it is a
///     mechanism with its own invariant — a newer load cancels and owns the shell,
///     so a slow read can never land its result on top of a profile opened after it.
///
///     The session does no binding of its own. Outcomes are handed to the callbacks
///     the shell installs once at construction; every path ends in exactly one of
///     them, so the shell's state machine has no hidden writers.
///
///     Whether the backlog file still holds what the profile was opened from is a
///     poll rather than a <c>FileSystemWatcher</c>: the watcher's events are
///     platform-specific, arrive several times for one save, and are silently
///     dropped on network shares and some container filesystems — so a guard built
///     on it would be least reliable exactly where a shared backlog is most likely.
///     Comparing the content hash answers the real question directly, and an editor
///     that rewrites identical bytes is correctly reported as no change (ABSD-504).
/// </summary>
public sealed class ProfileSession
{
    private readonly ProfileLoader _loader;

    /// <summary>Cancels the load in flight when another starts.</summary>
    private CancellationTokenSource? _loading;

    /// <summary>
    ///     The workspace was adopted. Every route to a current profile lands here —
    ///     open, reload, save, an accepted agent edit, a profile switch.
    /// </summary>
    public Action<BacklogWorkspace> Adopted { get; }

    /// <summary>An open from a path failed: (config path, formatted error).</summary>
    public Action<string, string> OpenFailed { get; }

    /// <summary>The onboarding route's open failed; the message stays on that screen.</summary>
    public Action<string> OnboardingOpenFailed { get; }

    /// <summary>A reload of the open profile failed: formatted error.</summary>
    public Action<string> ReloadFailed { get; }

    /// <summary>The poll found the file changed: the shell raises its banner.</summary>
    public Action StaleDetected { get; }

    public ProfileSession(
        ProfileLoader loader,
        Action<BacklogWorkspace> adopted,
        Action<string, string> openFailed,
        Action<string> onboardingOpenFailed,
        Action<string> reloadFailed,
        Action staleDetected)
    {
        _loader = loader;
        Adopted = adopted;
        OpenFailed = openFailed;
        OnboardingOpenFailed = onboardingOpenFailed;
        ReloadFailed = reloadFailed;
        StaleDetected = staleDetected;
    }

    /// <summary>The token the in-flight load carries, for reads scoped to it.</summary>
    public CancellationToken LoadToken => _loading?.Token ?? CancellationToken.None;

    /// <summary>Loads a Board profile, replacing whatever is currently shown.</summary>
    public async Task LoadAsync(string configPath)
    {
        var token = BeginLoad();

        Result<BacklogWorkspace> loaded;
        try
        {
            loaded = await _loader.LoadAsync(configPath, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // A newer load owns the shell now; touching a bound property here would
            // overwrite what that load already put on screen.
            return;
        }

        if (token.IsCancellationRequested)
        {
            return;
        }

        if (loaded.IsFailure)
        {
            var error = loaded.Error!;
            OpenFailed(configPath, $"{error.SafeMessage} ({error.Code})");
            return;
        }

        Adopted(loaded.Value);
    }

    /// <summary>
    ///     Opens a Board profile from the onboarding screen's "I have a
    ///     board.config.json" route. When no profile is open, a failure stays on the
    ///     first-run screen and is reported beside the route that produced it —
    ///     replacing the form with a blank error page would strand a new user.
    /// </summary>
    public async Task OpenFromOnboardingAsync(string configPath)
    {
        var token = BeginLoad();

        Result<BacklogWorkspace> loaded;
        try
        {
            loaded = await _loader.LoadAsync(configPath, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (token.IsCancellationRequested)
        {
            return;
        }

        if (loaded.IsFailure)
        {
            var error = loaded.Error!;
            OnboardingOpenFailed($"{error.SafeMessage} ({error.Code})");
            return;
        }

        Adopted(loaded.Value);
    }

    /// <summary>
    ///     Re-reads the open profile from its own files, picking up external edits —
    ///     the route for a profile with no config path (described in onboarding).
    /// </summary>
    public async Task ReloadAsync(BacklogWorkspace current)
    {
        var token = BeginLoad();

        Result<BacklogWorkspace> again;
        try
        {
            again = await _loader.ReloadAsync(current, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (token.IsCancellationRequested)
        {
            return;
        }

        if (again.IsFailure)
        {
            var error = again.Error!;
            ReloadFailed($"{error.SafeMessage} ({error.Code})");
            return;
        }

        Adopted(again.Value);
    }

    /// <summary>
    ///     Whether the backlog file still holds what this profile was opened from
    ///     (ABSD-504, PRD-AC-15). Returns true when staleness was newly detected.
    ///
    ///     A read that failed is not evidence of a change: a file being written at
    ///     the moment we looked, or a share that dropped, would otherwise raise a
    ///     banner that clears itself a second later. An already-stale profile is not
    ///     re-read: only a reload clears the flag, so re-polling could spend a file
    ///     read to learn nothing.
    /// </summary>
    public async Task<bool> CheckForExternalChangeAsync(BacklogWorkspace? workspace, bool alreadyStale)
    {
        if (workspace is not { } current || alreadyStale)
        {
            return false;
        }

        var stamped = await _loader.StampAsync(current.BacklogPath).ConfigureAwait(true);
        if (stamped.IsFailure)
        {
            return false;
        }

        if (stamped.Value.ContentDiffersFrom(current.Stamp))
        {
            StaleDetected();
            return true;
        }

        return false;
    }

    /// <summary>
    ///     Splices nothing here: the caller hands the spliced markdown in, and this
    ///     writes it through the loader. A save belongs to the session because it is
    ///     a file operation with an ABSD-507 event, like every other one here.
    /// </summary>
    public Task<Result<BacklogWorkspace>> SaveAsync(BacklogWorkspace workspace, string markdown) =>
        _loader.SaveAsync(workspace, markdown);

    /// <summary>The import CSV write, for the shell's export command (ABSD-207).</summary>
    public Task<Result<CsvExport>> ExportCsvAsync(BacklogWorkspace workspace, string destinationPath) =>
        _loader.ExportCsvAsync(workspace, destinationPath);

    /// <summary>True when something is already at this path, for the overwrite prompt.</summary>
    public bool Exists(string path) => _loader.Exists(path);

    /// <summary>Starts a load, cancelling whatever was still in flight.</summary>
    private CancellationToken BeginLoad()
    {
        _loading?.Cancel();
        _loading?.Dispose();
        _loading = new CancellationTokenSource();
        return _loading.Token;
    }
}
