using AdoBoardSync.Core.Results;

namespace AdoBoardSync.Core.Board;

/// <summary>
///     One local branch holding commit evidence for an Issue code: the branch names
///     the code, and it holds commits the base ref does not. The port
///     <see cref="IGitEvidenceSource" /> returns these; <c>PlanBuilder.BuildAdvance</c>
///     turns them into state rows.
/// </summary>
public sealed record GitBranchEvidence(string Repo, string Branch, string Code, int AheadCount)
{
    /// <summary>What a Plan row's <see cref="PlanRow.Detail" /> shows, matching the CLI's plan line.</summary>
    public string Describe()
    {
        return $"{Branch} (+{AheadCount})";
    }
}

/// <summary>What one probe of every repository found, including what it had to skip.</summary>
public sealed record GitProbeReport(
    IReadOnlyList<GitBranchEvidence> Evidence,
    IReadOnlyList<string> SkippedRepos)
{
    public static GitProbeReport Empty { get; } = new([], []);
}

/// <summary>
///     Reads local git repositories for commit evidence, the CLI's <c>gitstate</c>
///     side. Declared in Core so <c>BuildAdvance</c> stays pure; implemented in
///     Infrastructure over the <c>git</c> CLI. A failed fetch disqualifies a
///     repository rather than being skipped silently: probing stale refs would read
///     as unstarted work that has actually begun.
/// </summary>
public interface IGitEvidenceSource
{
    /// <summary>
    ///     Probes each repository for branches whose name matches
    ///     <paramref name="codePattern" /> and hold commits beyond
    ///     <paramref name="baseRef" />. Reads only; never mutates a repository.
    /// </summary>
    Task<Result<GitProbeReport>> ProbeAsync(
        IReadOnlyList<string> repos,
        string baseRef,
        bool fetch,
        string codePattern,
        CancellationToken cancellationToken = default);
}

/// <summary>
///     The stand-in when no source was supplied to the view model — the same refusal
///     pattern as <c>UnconfiguredBoardGateway</c>: a missing registration must fail
///     loudly, not quietly probe nothing.
/// </summary>
public sealed class UnconfiguredGitEvidenceSource : IGitEvidenceSource
{
    public Task<Result<GitProbeReport>> ProbeAsync(
        IReadOnlyList<string> repos,
        string baseRef,
        bool fetch,
        string codePattern,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<Result<GitProbeReport>>(Error.SourceFailure(
            "advance.not_configured",
            "No git evidence source was supplied to this surface."));
    }
}