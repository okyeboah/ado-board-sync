namespace AdoBoardSync.Core.Configuration;

/// <summary>One place a Personal Access Token can come from.</summary>
public interface IPatSource
{
    /// <summary>A short name for the source, safe to show in an error. Never the token.</summary>
    string Name { get; }

    /// <summary>Returns the token, or null when this source holds none.</summary>
    string? TryRead();
}

/// <summary>Reads the PAT from the environment variable named by <c>pat_env</c>.</summary>
public sealed class EnvironmentPatSource(string variableName) : IPatSource
{
    public string Name => $"environment variable {variableName}";

    public string? TryRead()
    {
        var value = Environment.GetEnvironmentVariable(variableName);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

/// <summary>Reads the PAT from the gitignored token file named by <c>pat_file</c>.</summary>
public sealed class FilePatSource(string path) : IPatSource
{
    public string Name => $"token file {path}";

    public string? TryRead()
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var token = File.ReadAllText(path).Trim();
        return token.Length > 0 ? token : null;
    }
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
    /// <summary>The CLI-compatible order: the environment variable, then the token file.</summary>
    public static PatResolver ForConfig(BoardConfig config) =>
        new([
            new EnvironmentPatSource(config.PatEnv),
            new FilePatSource(config.ResolvePath(config.PatFile))
        ]);

    public IReadOnlyList<IPatSource> Sources => sources;

    /// <summary>Returns the first token found, or null. Never logged.</summary>
    public string? Resolve()
    {
        foreach (var source in sources)
        {
            if (source.TryRead() is { } token)
            {
                return token;
            }
        }

        return null;
    }

    /// <summary>
    /// Names the sources that were checked, for an error message when none held a
    /// token. Names only — a source never reveals a value it did hold.
    /// </summary>
    public string DescribeSources() => string.Join(", ", sources.Select(source => source.Name));
}
