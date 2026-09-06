using AdoBoardSync.Core.Diagnostics;

namespace AdoBoardSync.TestKit;

/// <summary>
/// Holds events in memory so a test can assert on what an operation recorded.
///
/// It records verbatim: no redaction pass runs here. That is on purpose — a test
/// proving <see cref="DiagnosticRedaction"/> works has to be able to see what the
/// caller actually handed over, and a sink that quietly cleaned it up first would
/// make a broken redactor look correct.
///
/// It lives in the TestKit rather than in Infrastructure, where it used to ship
/// inside the application assembly under a comment reading "do not wire this into
/// the application". A rule kept by a comment is kept until somebody does not read
/// it; here the assembly boundary keeps it instead.
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
