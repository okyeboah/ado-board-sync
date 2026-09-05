using AdoBoardSync.Core.Diagnostics;

namespace AdoBoardSync.Infrastructure.Diagnostics;

/// <summary>
/// Fans one event out to several sinks (ABSD-507) — typically the rolling file and,
/// in a development build, a console or an in-memory tail.
///
/// A sink that throws does not stop the others. The composite is the seam where one
/// misbehaving sink would otherwise take the whole diagnostic pipeline down with it,
/// and losing every log because one of them broke is the failure this guards.
/// </summary>
public sealed class CompositeDiagnostics : IDiagnostics
{
    private readonly IDiagnostics[] _sinks;

    public CompositeDiagnostics(params IDiagnostics[] sinks)
    {
        ArgumentNullException.ThrowIfNull(sinks);
        _sinks = [.. sinks];
    }

    public void Write(DiagnosticEvent diagnosticEvent)
    {
        foreach (var sink in _sinks)
        {
            try
            {
                sink.Write(diagnosticEvent);
            }
            catch (Exception)
            {
                // Nothing to report it to: reporting a diagnostics failure through
                // diagnostics is the loop this whole subsystem must not enter.
            }
        }
    }
}

/// <summary>
/// Holds events in memory so a test can assert on what an operation recorded.
///
/// It records verbatim: no redaction pass runs here. That is on purpose — a test
/// proving <see cref="DiagnosticRedaction"/> works has to be able to see what the
/// caller actually handed over, and a sink that quietly cleaned it up first would
/// make a broken redactor look correct. Do not wire this into the application.
/// </summary>
public sealed class InMemoryDiagnostics : IDiagnostics
{
    private readonly Lock _gate = new();
    private readonly List<DiagnosticEvent> _events = [];

    public IReadOnlyList<DiagnosticEvent> Events
    {
        get
        {
            lock (_gate)
            {
                return [.. _events];
            }
        }
    }

    public void Write(DiagnosticEvent diagnosticEvent)
    {
        lock (_gate)
        {
            _events.Add(diagnosticEvent);
        }
    }
}
