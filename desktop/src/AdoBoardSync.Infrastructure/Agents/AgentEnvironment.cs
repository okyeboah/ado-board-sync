using AdoBoardSync.Core.Configuration;

namespace AdoBoardSync.Infrastructure.Agents;

/// <summary>
/// The rule every agent subprocess is started under: the board's Personal Access
/// Token does not reach it (ABSD-702).
///
/// Not adding the token is not enough. A <see cref="System.Diagnostics.ProcessStartInfo" />
/// is pre-populated with this process's own environment, so a desktop launched from
/// a shell that exported <c>AZURE_DEVOPS_PAT</c> would hand the token to every agent
/// it spawns without a line of code asking it to. The variable has to be removed.
///
/// What is deliberately left alone is the agent's own credential — an
/// <c>ANTHROPIC_API_KEY</c> and its equivalents. ABSD-701 drives a CLI the user has
/// already authenticated; stripping the provider's key would break the run this app
/// exists to start.
/// </summary>
internal static class AgentEnvironment
{
    /// <summary>
    /// The board token's variable names. <c>pat_env</c>'s default, plus the aliases a
    /// machine set up for Azure DevOps commonly also exports under: the az CLI's
    /// devops extension reads <c>AZURE_DEVOPS_EXT_PAT</c>, and a pipeline agent
    /// exports <c>SYSTEM_ACCESSTOKEN</c>. One machine often holds the same token
    /// under several of them, so stripping only the configured name leaves a copy
    /// behind.
    /// </summary>
    internal static IReadOnlyList<string> PatVariableNames { get; } =
    [
        BoardConfig.DefaultPatEnv,
        "AZURE_DEVOPS_EXT_PAT",
        "AZURE_DEVOPS_TOKEN",
        "SYSTEM_ACCESSTOKEN",
    ];

    /// <summary>Removes every named variable from a child process's environment.</summary>
    internal static void StripPat(IDictionary<string, string?> environment, IEnumerable<string> names)
    {
        var unwanted = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

        // Matched without regard to case on every platform, even though POSIX
        // environment names are case-sensitive: removing azure_devops_pat as well as
        // AZURE_DEVOPS_PAT can only remove too much, and too much is the safe
        // direction for a credential. Keys are copied first because removing from the
        // dictionary being enumerated throws.
        foreach (var name in environment.Keys.Where(unwanted.Contains).ToList())
        {
            environment.Remove(name);
        }
    }
}
