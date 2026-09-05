using AdoBoardSync.Core.Results;

namespace AdoBoardSync.Core.Agents;

/// <summary>
/// An agent CLI this app knows how to drive. The app spawns a binary the user has
/// already installed and authenticated; it holds no provider credential of its
/// own, exactly as ARCHITECTURE.md §6 requires of the PAT (ABSD-701).
/// </summary>
public sealed record AgentProvider(string Id, string DisplayName, string Executable, string VersionArgument)
{
    /// <summary>The providers ABSD-701 names. Order is the order the picker offers them.</summary>
    public static IReadOnlyList<AgentProvider> Known { get; } =
    [
        new("claude", "Claude Code", "claude", "--version"),
        new("codex", "Codex CLI", "codex", "--version"),
        new("opencode", "OpenCode", "opencode", "--version"),
        new("gemini", "Gemini CLI", "gemini", "--version"),
    ];
}

/// <summary>One provider found on this machine, with the version it reported.</summary>
public sealed record InstalledAgent(AgentProvider Provider, string ExecutablePath, string Version)
{
    public string Display => $"{Provider.DisplayName} {Version}";
}

/// <summary>Which agent CLIs are installed here. A pure probe: it runs each
/// candidate's version flag and nothing else.</summary>
public interface IAgentProviderRegistry
{
    Task<Result<IReadOnlyList<InstalledAgent>>> DiscoverAsync(CancellationToken cancellationToken = default);
}

/// <summary>What the prompt applies to, stated to the user before the run.</summary>
public enum AgentScope
{
    /// <summary>The selected Epic and everything under it.</summary>
    Epic,

    /// <summary>The selected Issue and its bullets.</summary>
    Issue,

    /// <summary>The whole backlog file.</summary>
    Backlog,
}

/// <summary>
/// One request to run an agent. <see cref="WorkingDirectory"/> is the open
/// profile's directory: the agent sees the backlog and nothing above it.
/// </summary>
public sealed record AgentRunRequest
{
    public required InstalledAgent Agent { get; init; }

    public required string Prompt { get; init; }

    public required string WorkingDirectory { get; init; }

    public required AgentScope Scope { get; init; }

    /// <summary>The scoped item's code or title, for the record and the prompt header.</summary>
    public string? ScopeLabel { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);
}

/// <summary>How an agent run ended.</summary>
public enum AgentRunStatus
{
    Succeeded,
    Failed,
    TimedOut,
    Cancelled,
}

public sealed record AgentRunResult
{
    public required AgentRunStatus Status { get; init; }

    public required int ExitCode { get; init; }

    public string StandardOutput { get; init; } = string.Empty;

    public string StandardError { get; init; } = string.Empty;

    public TimeSpan Duration { get; init; }

    public bool Succeeded => Status == AgentRunStatus.Succeeded;
}

/// <summary>
/// Runs an agent CLI as a subprocess (ABSD-702). The PAT never reaches the child's
/// environment, arguments or stdin — implementations must strip it rather than
/// merely not add it, because the parent process may hold it in its own
/// environment.
/// </summary>
public interface IAgentRunner
{
    Task<Result<AgentRunResult>> RunAsync(
        AgentRunRequest request,
        IProgress<string>? output = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// One agent run as the history store holds it (ABSD-706). Recorded whether or not
/// the edit was accepted: an agent that produces changes nobody keeps is exactly
/// the pattern this record exists to make visible.
/// </summary>
public sealed record AgentRunRecord
{
    public long Id { get; init; }

    public required string ProfileKey { get; init; }

    public required string ProviderId { get; init; }

    public required string ProviderVersion { get; init; }

    public required string Prompt { get; init; }

    public required string Scope { get; init; }

    public string? ScopeLabel { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? FinishedAt { get; init; }

    public required string Status { get; init; }

    public int ExitCode { get; init; }

    /// <summary>Null while the diff is still under review; then true or false.</summary>
    public bool? EditAccepted { get; init; }

    public string Summary { get; init; } = string.Empty;
}

/// <summary>Agent runs live in the same local store as ApplyRuns (ABSD-706).</summary>
public interface IAgentRunHistory
{
    Task<Result<long>> RecordRunAsync(AgentRunRecord record, CancellationToken cancellationToken = default);

    /// <summary>Sets the accept/reject verdict once the diff has been reviewed.</summary>
    Task<Result<bool>> RecordVerdictAsync(
        long runId,
        bool accepted,
        DateTimeOffset finishedAt,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<AgentRunRecord>>> ListRunsAsync(
        string profileKey,
        int limit,
        CancellationToken cancellationToken = default);
}
