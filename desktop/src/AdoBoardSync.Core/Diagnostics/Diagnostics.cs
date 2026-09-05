using System.Collections.ObjectModel;

namespace AdoBoardSync.Core.Diagnostics;

public enum DiagnosticLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>
/// One structured record of something the app did (ARCHITECTURE.md §7). It is a
/// record with named fields rather than a formatted line so a diagnostics bundle
/// can be filtered and compared rather than only read.
///
/// <see cref="Code"/> is the FSD §5.1 error code when the event reports a failure,
/// which is what makes a support conversation start from the same vocabulary the
/// user saw in the status bar.
/// </summary>
public sealed record DiagnosticEvent
{
    public required DateTimeOffset Timestamp { get; init; }

    public required DiagnosticLevel Level { get; init; }

    /// <summary>The subsystem: "plan", "apply", "backlog", "config", "agent".</summary>
    public required string Category { get; init; }

    public required string Message { get; init; }

    /// <summary>The typed error code, when this event reports a failure.</summary>
    public string? Code { get; init; }

    /// <summary>
    /// Structured detail. Values are redacted before they reach a sink — see
    /// <see cref="DiagnosticRedaction"/> — so a secret that reaches this dictionary
    /// by mistake still does not reach a file.
    /// </summary>
    public IReadOnlyDictionary<string, string> Data { get; init; } =
        ReadOnlyDictionary<string, string>.Empty;
}

/// <summary>
/// Where diagnostics go. Implementations must not throw: a diagnostics failure
/// must never become the failure the user sees instead of the real one.
/// </summary>
public interface IDiagnostics
{
    void Write(DiagnosticEvent diagnosticEvent);
}

/// <summary>A sink that drops everything. The default, so nothing is required to
/// configure diagnostics before it can run.</summary>
public sealed class NullDiagnostics : IDiagnostics
{
    public static readonly NullDiagnostics Instance = new();

    private NullDiagnostics()
    {
    }

    public void Write(DiagnosticEvent diagnosticEvent)
    {
    }
}
