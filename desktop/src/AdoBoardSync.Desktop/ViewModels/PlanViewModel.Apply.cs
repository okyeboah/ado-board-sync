using System.Diagnostics;
using AdoBoardSync.Core.Diagnostics;
using AdoBoardSync.Core.Planning;
using AdoBoardSync.Desktop.Services;

namespace AdoBoardSync.Desktop.ViewModels;

/// <summary>
/// The Apply half of the Plan/Apply gate: confirmation, execution, and the two
/// refusals (unsaved edits, stale profile) that hold even after a confirmation.
/// Split out as a partial because the generation half and the write half answer
/// different questions; the class is one gate.
/// </summary>
public sealed partial class PlanViewModel
{
    /// <summary>
    ///     Opens the confirmation step. It never writes anything itself.
    ///     PRD-AC-03: malformed backlog markup blocks Apply before a confirmation is
    ///     ever offered. The workspace carries the offline audit total, so the gate
    ///     reads the same number the tree badges and the problems card show.
    /// </summary>
    public void RequestApply(BacklogWorkspace? workspace)
    {
        if (Plan?.HasWork != true) return;

        if (workspace is { MarkupProblemCount: > 0 } blocked)
        {
            ErrorText = blocked.MarkupProblemCount == 1
                ? "The backlog has 1 markup problem. Fix it — check-html would fail too — then generate the Plan again. (markup.invalid)"
                : $"The backlog has {blocked.MarkupProblemCount} markup problems. Fix them — check-html would fail too — then generate the Plan again. (markup.invalid)";
            StatusText = "Apply is blocked until the markup problems are fixed.";
            IsConfirming = false;
            return;
        }

        IsConfirming = true;
    }

    public void CancelApply()
    {
        IsConfirming = false;
    }

    /// <summary>
    ///     Executes the confirmed Plan. The fresh board read is the staleness check
    ///     only — the rows applied are the reviewed ones, never recomputed from it.
    ///     The markup gate runs again here: the confirmation dialog is not the only
    ///     line of defence, so removing one still leaves the other.
    /// </summary>
    public async Task ApplyConfirmedAsync(BacklogWorkspace workspace, CancellationToken cancellationToken = default)
    {
        if (Plan is not { } plan || !IsConfirming) return;

        if (BlockedByUnsavedEdits("applying"))
        {
            return;
        }

        if (workspace.MarkupProblemCount > 0)
        {
            ErrorText =
                $"The backlog has {workspace.MarkupProblemCount} markup problem(s). Fix them before applying. (markup.invalid)";
            StatusText = "Apply refused.";
            IsConfirming = false;
            return;
        }

        var token = await ResolveTokenAsync(workspace.Config, cancellationToken).ConfigureAwait(true);
        if (token is null)
        {
            ErrorText = CredentialStatus;
            return;
        }

        IsBusy = true;
        IsConfirming = false;
        ErrorText = null;
        Outcomes.Clear();
        StatusText = "Applying…";

        try
        {
            var gateway = _gatewayFactory(token);
            try
            {
                var fresh = await gateway.ReadAsync(workspace.Config, cancellationToken);
                if (fresh.IsFailure)
                {
                    ErrorText = $"{fresh.Error!.SafeMessage} ({fresh.Error.Code})";
                    StatusText = "Could not verify the board before applying.";
                    _diagnostics.OperationFailed("apply", fresh.Error);
                    return;
                }

                var currentBacklog = PlanBuilder.FingerprintBacklog(workspace.Markdown);

                // The run is opened before the first write and closed after the
                // last, so an app that dies mid-Apply leaves an open run — which
                // is the honest record of what happened, and the one a user comes
                // to the History view looking for.
                var startedAt = DateTimeOffset.UtcNow;
                var startedApplying = Stopwatch.GetTimestamp();
                if (_recorder is { } recorder)
                {
                    await recorder.BeginAsync(
                        workspace.ProfileKey, plan.Command, startedAt, cancellationToken);
                }

                // Two observers, because they need opposite things. The list is
                // bound, so it must be touched on the UI thread, which is what
                // Progress<T> is for. The recorder must not be: Progress<T> posts
                // to the dispatcher and returns, so a callback that started the
                // history write would not have run yet when the run is closed
                // below — and a completed run refuses outcomes, silently dropping
                // the rows PRD-AC-08 promises. Recording therefore happens inline
                // on the thread that reported the outcome, which puts the write on
                // the recorder's chain before ApplyAsync returns.
                var ui = new Progress<ApplyOutcome>(Outcomes.Add);
                var progress = new RecordingProgress(ui, _recorder, cancellationToken);

                // Before the first round trip, so a process that dies mid-Apply
                // still leaves the record that a write was in flight.
                _diagnostics.ApplyStarted(plan);

                var report = await ApplyExecutor.ApplyAsync(
                    gateway, workspace.Config, plan,
                    currentBacklog, fresh.Value.Fingerprint,
                    progress, cancellationToken);

                if (report.IsFailure)
                {
                    ErrorText = $"{report.Error!.SafeMessage} ({report.Error.Code})";
                    StatusText = "Apply refused.";
                    _diagnostics.OperationFailed("apply", report.Error);

                    // Refused before the first write, so there is no run to close.
                    // Abandoning leaves the opened row unfinished rather than
                    // claiming a clean end to something that never ran.
                    _recorder?.Abandon();

                    // The approved Plan no longer describes the board.
                    Plan = null;
                    Rows.Clear();
                    return;
                }

                if (_recorder is { } closing)
                {
                    await closing.CompleteAsync(
                        report.Value.Summary, DateTimeOffset.UtcNow, cancellationToken);
                }

                _diagnostics.ApplyFinished(
                    plan, report.Value, Stopwatch.GetElapsedTime(startedApplying));

                StatusText = report.Value.Summary;

                // The board has moved; a further write needs a fresh Plan.
                Plan = null;
                Rows.Clear();
            }
            finally
            {
                (gateway as IDisposable)?.Dispose();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    ///     The unsaved-edits half of the gate. Reports the refusal and returns true
    ///     when the editor holds work the file does not — the same file every Plan
    ///     and every Apply fingerprint is computed from.
    /// </summary>
    private bool BlockedByUnsavedEdits(string action)
    {
        if (UnsavedEditsCheck?.Invoke() == true)
        {
            ErrorText =
                "The backlog has unsaved edits. Save them first — a Plan is computed from "
                + "the file, and the file is the source of truth. (backlog.unsaved)";
            StatusText = $"Save the backlog before {action}.";
            return true;
        }

        // Checked second because it is the rarer of the two, and because a user with
        // unsaved edits needs to hear about those first — reloading would discard
        // them, so telling them to reload before telling them they have work in the
        // buffer would invite exactly the wrong action (ABSD-504).
        if (StaleProfileCheck?.Invoke() == true)
        {
            ErrorText =
                "The backlog file has changed on disk since this profile was opened. "
                + "Reload before continuing — otherwise the Plan would be computed from "
                + "text this app no longer holds. (backlog.stale)";
            StatusText = $"Reload the backlog before {action}.";
            return true;
        }

        return false;
    }
}
