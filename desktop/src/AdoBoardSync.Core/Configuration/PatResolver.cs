using AdoBoardSync.Core.Results;

namespace AdoBoardSync.Core.Configuration;

/// <summary>One place a Personal Access Token can come from.</summary>
public interface IPatSource
{
    /// <summary>A short name for the source, safe to show in an error. Never the token.</summary>
    string Name { get; }

    /// <summary>
    /// The token, a null value when this source simply holds none, or a failure when
    /// the source exists but could not be read — a token file locked by another
    /// process, a keychain the user cancelled. Those are three different answers and
    /// the credential badge says something different for each, so they are not
    /// collapsed into one null (ABSD-110).
    /// </summary>
    Result<string?> TryRead();
}

/// <summary>Reads the PAT from the environment variable named by <c>pat_env</c>.</summary>
public sealed class EnvironmentPatSource(string variableName) : IPatSource
{
    public string Name => $"environment variable {variableName}";

    public Result<string?> TryRead()
    {
        var value = Environment.GetEnvironmentVariable(variableName);
        return string.IsNullOrWhiteSpace(value) ? (string?)null : value.Trim();
    }
}

/// <summary>Reads the PAT from the gitignored token file named by <c>pat_file</c>.</summary>
public sealed class FilePatSource(string path) : IPatSource
{
    public string Name => $"token file {path}";

    public Result<string?> TryRead()
    {
        if (!File.Exists(path))
        {
            return (string?)null;
        }

        string token;
        try
        {
            token = File.ReadAllText(path).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The one place the library's Result discipline used to break: an
            // unguarded read here threw straight through a resolver every caller
            // treats as total, so a locked, permission-denied or mid-delete token
            // file crashed the action instead of naming itself in the badge.
            return Error.SourceFailure(
                "credential.file_unreadable",
                $"Could not read the token file {path}: {ex.Message}");
        }

        return token.Length > 0 ? token : (string?)null;
    }
}

/// <summary>
/// What one resolution attempt found: the token if any, which source produced it,
/// and every source that failed on the way. The failures travel with the result
/// because a badge reading "no token found" while a keychain was quietly refusing
/// is worse than no badge at all (ABSD-110).
/// </summary>
public sealed record PatResolution(string? Token, string? SourceName, IReadOnlyList<Error> Failures)
{
    public bool Found => Token is not null;

    public bool HasFailures => Failures.Count > 0;
}

/// <summary>
/// Resolves a PAT from an ordered list of sources, first match wins.
///
/// The architecture treats configuration and credentials as separate concerns, so
/// this deliberately does not live on <see cref="BoardConfig"/>. It is a list
/// rather than a fixed chain because Infrastructure prepends an operating-system
/// credential-store source, which Core cannot reference; the CLI-compatible
/// environment and file sources stay here so a project already set up for the CLI
/// keeps working untouched.
///
/// The token is read on each call and never cached, so a rotated token takes
/// effect without a restart and no long-lived object holds the secret.
/// </summary>
public sealed class PatResolver(IReadOnlyList<IPatSource> sources)
{
    /// <summary>
    /// The CLI-compatible order, with ABSD-103's addition in front: the operating
    /// system's credential store, then <c>pat_env</c>, then <c>pat_file</c>. A
    /// platform with no usable store contributes no source at all rather than one
    /// that always misses, so <see cref="DescribeSources"/> does not list a place
    /// the user could not have put anything.
    /// </summary>
    public static PatResolver ForConfig(BoardConfig config, ICredentialStore? credentialStore = null)
    {
        var sources = new List<IPatSource>();

        if (credentialStore is { IsAvailable: true })
        {
            sources.Add(new CredentialStorePatSource(credentialStore, CredentialKey(config)));
        }

        sources.Add(new EnvironmentPatSource(config.PatEnv));
        sources.Add(new FilePatSource(config.ResolvePath(config.PatFile)));
        return new PatResolver(sources);
    }

    /// <summary>
    /// The key one profile's token is stored under. Keyed by organisation and
    /// project rather than by config path, so moving the config file does not
    /// orphan the token and two profiles can never share one entry.
    /// </summary>
    public static string CredentialKey(BoardConfig config) =>
        $"ado-board-sync:{config.Org}/{config.Project}";

    public IReadOnlyList<IPatSource> Sources => sources;

    /// <summary>Returns the first token found, or null. Never logged.</summary>
    public string? Resolve() => ResolveDetailed().Token;

    /// <summary>
    /// The same walk, keeping what it learned: which source answered, and every
    /// source that failed rather than simply holding nothing.
    /// </summary>
    public PatResolution ResolveDetailed()
    {
        var failures = new List<Error>();

        foreach (var source in sources)
        {
            var read = source.TryRead();
            if (read.IsFailure)
            {
                // A source that is broken does not stop the walk: the next one may
                // well hold the token, and the CLI keeps working when a keychain
                // does not. The failure is reported, not thrown.
                failures.Add(read.Error!);
                continue;
            }

            if (read.Value is { } token)
            {
                return new PatResolution(token, source.Name, failures);
            }
        }

        return new PatResolution(null, null, failures);
    }

    /// <summary>
    /// Names the sources that were checked, for an error message when none held a
    /// token. Names only — a source never reveals a value it did hold.
    /// </summary>
    public string DescribeSources() => string.Join(", ", sources.Select(source => source.Name));
}
