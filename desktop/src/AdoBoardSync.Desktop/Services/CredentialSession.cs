using AdoBoardSync.Core.Configuration;

namespace AdoBoardSync.Desktop.Services;

/// <summary>
///     The one credential chain every board-reading surface walks: a token typed
///     this session first, then the operating system's credential store, then the
///     CLI's environment variable and token file. Stated once, because two surfaces
///     resolving credentials differently is exactly the bug a user cannot diagnose —
///     the Plan gate's badge and the Audit badge had already drifted apart in what
///     they reported about a failed source.
///
///     Resolution runs off the caller's thread: reading the OS store spawns a child
///     process, and on a locked keychain that child blocks on an unlock prompt for
///     up to its timeout — on the render thread it freezes the window.
/// </summary>
public sealed class CredentialSession
{
    private readonly ICredentialStore _credentialStore;

    /// <summary>Which store this session uses. The name never carries a secret, and
    /// it is how a test proves the composition root's store arrived (ABSD-106).</summary>
    public string StoreName => _credentialStore.Name;

    public CredentialSession(ICredentialStore? credentialStore = null)
    {
        // Not OsCredentialStore.ForThisPlatform(): the composition root injects the
        // platform's store, and a view model built outside the container gets the
        // empty one rather than a quiet reach into the user's keychain.
        _credentialStore = credentialStore
            ?? new UnavailableCredentialStore("no credential store was supplied");
    }

    /// <summary>The sources for one profile, in order, with the session token in front.</summary>
    public PatResolver ResolverFor(BoardConfig config, string? sessionToken)
    {
        var sources = new List<IPatSource>();
        if (!string.IsNullOrWhiteSpace(sessionToken))
        {
            sources.Add(new SessionPatSource(sessionToken));
        }

        sources.AddRange(PatResolver.ForConfig(config, _credentialStore).Sources);
        return new PatResolver(sources);
    }

    /// <summary>
    ///     The same walk, off the calling thread. Each source is read exactly once.
    /// </summary>
    public async Task<(PatResolver Resolver, PatResolution Resolution)> ResolveAsync(
        BoardConfig config, string? sessionToken, CancellationToken cancellationToken = default)
    {
        var resolver = ResolverFor(config, sessionToken);
        var resolution = await Task.Run(resolver.ResolveDetailed, cancellationToken).ConfigureAwait(false);
        return (resolver, resolution);
    }

    /// <summary>
    ///     What the badge says. A source that failed is named separately from one that
    ///     simply held nothing: "checked and empty" and "checked and refused" call for
    ///     different fixes, and collapsing them sends the user to the wrong one.
    /// </summary>
    public static string Describe(PatResolver resolver, PatResolution resolution)
    {
        var trouble = resolution.HasFailures
            ? " " + string.Join(" ", resolution.Failures.Select(f => $"{f.SafeMessage} ({f.Code})"))
            : string.Empty;

        return resolution.Found
            ? $"Token resolved from {resolution.SourceName}.{trouble}"
            : $"No personal access token found. Checked {resolver.DescribeSources()}.{trouble}";
    }
}
