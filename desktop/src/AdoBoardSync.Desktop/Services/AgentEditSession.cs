using AdoBoardSync.Core.Agents;
using AdoBoardSync.Core.Results;

namespace AdoBoardSync.Desktop.Services;

/// <summary>One prompt, scoped, against the open profile (ABSD-703).</summary>
public sealed record AgentEditRequest
{
    public required BacklogWorkspace Workspace { get; init; }

    public required InstalledAgent Agent { get; init; }

    public required string Prompt { get; init; }

    public required AgentScope Scope { get; init; }

    /// <summary>The Epic title or Issue code this is scoped to; null for the whole backlog.</summary>
    public string? ScopeLabel { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);
}

/// <summary>How a run ended, from the reviewer's point of view rather than the process's.</summary>
public enum AgentEditOutcome
{
    /// <summary>The agent changed the backlog and the change is waiting on a verdict.</summary>
    UnderReview,

    /// <summary>The agent finished and left the backlog exactly as it found it.</summary>
    NoChange,

    /// <summary>The run failed, timed out, or was cancelled. Nothing is under review.</summary>
    NoRun,

    /// <summary>The agent changed the backlog into something that would not parse.</summary>
    Refused,
}

/// <summary>
/// What one run produced. <see cref="RunId" /> is null only when the history store
/// itself refused the write — the diff is still reviewable then, but no verdict can
/// be attached to it, and <see cref="HistoryError" /> says why.
/// </summary>
public sealed record AgentEditProposal
{
    public required AgentEditOutcome Outcome { get; init; }

    public required AgentRunResult Run { get; init; }

    public required string Summary { get; init; }

    public long? RunId { get; init; }

    public AgentEditReview? Review { get; init; }

    /// <summary>Why the edit was not shown, when it was not.</summary>
    public Error? Refusal { get; init; }

    /// <summary>Set only when putting the file back as it was itself failed.</summary>
    public Error? RestoreError { get; init; }

    public Error? HistoryError { get; init; }

    public bool IsUnderReview => Outcome == AgentEditOutcome.UnderReview && Review is not null;
}

/// <summary>
/// Drives one agent run end to end: snapshot, run, validate, and then either keep
/// what the agent wrote or put the file back byte for byte (ABSD-704, ABSD-706).
///
/// Two invariants are why this is a service and not view-model code:
///
/// <list type="bullet">
/// <item>The file is only ever left changed by an explicit accept. A run that
/// failed, timed out or was cancelled is restored from the snapshot, because a
/// half-written backlog is not a proposal — the agent was interrupted mid-edit, and
/// the diff would describe an accident.</item>
/// <item>Every run reaches <see cref="IAgentRunHistory" />, including the ones
/// nobody keeps. A run whose edit was thrown away is exactly the pattern the record
/// exists to make visible, and the verdict is written once, after the decision.</item>
/// </list>
///
/// Nothing here touches the board. An accepted edit changes a file; the board
/// consequences are a Plan, and a Plan is still confirmed on its own surface
/// (ABSD-705).
/// </summary>
public sealed class AgentEditSession(
    IAgentRunner runner,
    IAgentEditFileStore files,
    IAgentRunHistory history)
{
    private readonly IAgentRunner _runner = runner;

    private readonly IAgentEditFileStore _files = files;

    private readonly IAgentRunHistory _history = history;

    /// <summary>
    /// States the scope and the one file the agent may change, above the user's own
    /// words — the same facts the prompt surface shows before the button is pressed
    /// (ABSD-703). An agent told a different scope from the one the user was shown
    /// would make that disclosure a lie.
    /// </summary>
    public static string ComposePrompt(AgentEditRequest request)
    {
        var scope = request.Scope switch
        {
            AgentScope.Epic => $"the Epic \"{request.ScopeLabel}\" and the Issues under it",
            AgentScope.Issue => $"the Issue {request.ScopeLabel}",
            _ => "the whole backlog",
        };

        return $"""
            You are editing the backlog file {request.Workspace.BacklogPath} in this directory.
            Change only that file, and within it only {scope}.
            Keep the heading structure: an Epic is a "## " heading, an Issue is a "### {request.Workspace.Config.CodePrefix}-<number>" heading, and a Task is a "- " bullet under an Issue.
            Do not touch Azure DevOps. A person reviews your edit as a diff, and every board change goes through a separate confirmation.

            {request.Prompt}
            """;
    }

    public async Task<Result<AgentEditProposal>> RunAsync(
        AgentEditRequest request,
        IProgress<string>? output = null,
        CancellationToken cancellationToken = default)
    {
        var workspace = request.Workspace;

        var before = await Task.Run(() => _files.ReadBytes(workspace.BacklogPath), CancellationToken.None)
            .ConfigureAwait(false);
        if (before.IsFailure)
        {
            return before.Error!;
        }

        var snapshot = new AgentEditSnapshot(workspace.BacklogPath, before.Value);

        // Refused before the agent starts rather than after: running a CLI against a
        // file this app could not decode would leave the user somewhere reject is not
        // honestly available.
        var originalText = snapshot.DecodeText();
        if (originalText.IsFailure)
        {
            return originalText.Error!;
        }

        var startedAt = DateTimeOffset.UtcNow;
        var run = await _runner.RunAsync(
            new AgentRunRequest
            {
                Agent = request.Agent,
                Prompt = ComposePrompt(request),
                WorkingDirectory = workspace.Config.BaseDirectory,
                Scope = request.Scope,
                ScopeLabel = request.ScopeLabel,
                Timeout = request.Timeout,
            },
            output,
            cancellationToken).ConfigureAwait(false);

        if (run.IsFailure)
        {
            // The run never started, so nothing can have changed the file. Recorded
            // anyway: "the CLI would not start" is attributable history too.
            var failure = run.Error!;
            var neverStarted = new AgentRunResult { Status = AgentRunStatus.Failed, ExitCode = -1 };
            var noStart = await RecordAsync(
                request, startedAt, neverStarted, accepted: false,
                $"Did not start: {failure.Code}").ConfigureAwait(false);

            return new AgentEditProposal
            {
                Outcome = AgentEditOutcome.NoRun,
                Run = neverStarted,
                Summary = failure.SafeMessage,
                RunId = noStart.Id,
                HistoryError = noStart.Error,
                Refusal = failure,
            };
        }

        var result = run.Value;
        if (result.Status != AgentRunStatus.Succeeded)
        {
            var putBack = Restore(snapshot);
            var stopped = result.Status switch
            {
                AgentRunStatus.Cancelled => "Cancelled. The backlog was put back as it was.",
                AgentRunStatus.TimedOut => "Timed out. The backlog was put back as it was.",
                _ => $"Exited {result.ExitCode}. The backlog was put back as it was.",
            };

            var incomplete = await RecordAsync(request, startedAt, result, accepted: false, stopped)
                .ConfigureAwait(false);

            return new AgentEditProposal
            {
                Outcome = AgentEditOutcome.NoRun,
                Run = result,
                Summary = stopped,
                RunId = incomplete.Id,
                HistoryError = incomplete.Error,
                RestoreError = putBack,
            };
        }

        var after = await Task.Run(() => _files.ReadBytes(workspace.BacklogPath), CancellationToken.None)
            .ConfigureAwait(false);
        if (after.IsFailure)
        {
            var unreadable = await RecordAsync(
                request, startedAt, result, accepted: false,
                $"Could not re-read the backlog: {after.Error!.Code}").ConfigureAwait(false);

            return new AgentEditProposal
            {
                Outcome = AgentEditOutcome.Refused,
                Run = result,
                Summary = after.Error!.SafeMessage,
                RunId = unreadable.Id,
                HistoryError = unreadable.Error,
                Refusal = after.Error,
            };
        }

        if (snapshot.Matches(after.Value))
        {
            var untouched = await RecordAsync(
                request, startedAt, result, accepted: false,
                "The agent made no change to the backlog.").ConfigureAwait(false);

            return new AgentEditProposal
            {
                Outcome = AgentEditOutcome.NoChange,
                Run = result,
                Summary = "The agent made no change to the backlog.",
                RunId = untouched.Id,
                HistoryError = untouched.Error,
            };
        }

        var review = AgentEditReview.Build(workspace.Config, snapshot, originalText.Value, after.Value);
        if (review.IsFailure)
        {
            // Refused, not shown. The file goes back before the message goes up, so
            // the state the user is told about is the state they are in.
            var putBack = Restore(snapshot);
            var refused = await RecordAsync(
                request, startedAt, result, accepted: false,
                $"Refused: {review.Error!.Code}").ConfigureAwait(false);

            return new AgentEditProposal
            {
                Outcome = AgentEditOutcome.Refused,
                Run = result,
                Summary = review.Error!.SafeMessage,
                RunId = refused.Id,
                HistoryError = refused.Error,
                Refusal = review.Error,
                RestoreError = putBack,
            };
        }

        var diff = review.Value.Diff;
        var pending = await RecordAsync(
            request, startedAt, result, accepted: null,
            $"Edit under review ({diff.Summary}).").ConfigureAwait(false);

        return new AgentEditProposal
        {
            Outcome = AgentEditOutcome.UnderReview,
            Run = result,
            Summary = $"{diff.Summary} — review the diff, then accept or reject it.",
            RunId = pending.Id,
            HistoryError = pending.Error,
            Review = review.Value,
        };
    }

    /// <summary>
    /// Keeps what the agent wrote. There is nothing to write here — the agent already
    /// changed the file — so accepting is the verdict, and the re-parsed items on the
    /// review are what the caller hands to the editor.
    /// </summary>
    public Task<Result<bool>> AcceptAsync(
        AgentEditProposal proposal, CancellationToken cancellationToken = default)
    {
        if (proposal.Review is null)
        {
            return Task.FromResult<Result<bool>>(Error.Validation(
                "agent.edit.nothing_to_accept",
                "There is no agent edit under review."));
        }

        return VerdictAsync(proposal, accepted: true, cancellationToken);
    }

    /// <summary>
    /// Puts the file back byte for byte from the snapshot taken before the run, then
    /// records the verdict. The restore is attempted first and its failure returned
    /// as the answer: the user's file matters more than the record of it.
    /// </summary>
    public async Task<Result<bool>> RejectAsync(
        AgentEditProposal proposal, CancellationToken cancellationToken = default)
    {
        if (proposal.Review is not { } review)
        {
            return Error.Validation(
                "agent.edit.nothing_to_reject",
                "There is no agent edit under review.");
        }

        if (Restore(review.Snapshot) is { } failure)
        {
            return failure;
        }

        return await VerdictAsync(proposal, accepted: false, cancellationToken).ConfigureAwait(false);
    }

    private Task<Result<bool>> VerdictAsync(
        AgentEditProposal proposal, bool accepted, CancellationToken cancellationToken)
    {
        if (proposal.RunId is not { } runId)
        {
            return Task.FromResult<Result<bool>>(Error.SourceFailure(
                "agent.edit.unrecorded",
                "This run was never recorded, so its verdict cannot be attached to it."));
        }

        return _history.RecordVerdictAsync(runId, accepted, DateTimeOffset.UtcNow, cancellationToken);
    }

    private Error? Restore(AgentEditSnapshot snapshot)
    {
        var written = _files.WriteBytes(snapshot.Path, snapshot.ToArray());
        return written.IsFailure ? written.Error : null;
    }

    /// <summary>
    /// Writes the run to the history. Deliberately not cancellable: a cancelled run
    /// is the one most worth recording, and passing the token that cancelled it would
    /// drop exactly that row.
    /// </summary>
    private async Task<(long? Id, Error? Error)> RecordAsync(
        AgentEditRequest request,
        DateTimeOffset startedAt,
        AgentRunResult result,
        bool? accepted,
        string summary)
    {
        var recorded = await _history.RecordRunAsync(
            new AgentRunRecord
            {
                ProfileKey = request.Workspace.ProfileKey,
                ProviderId = request.Agent.Provider.Id,
                ProviderVersion = request.Agent.Version,
                Prompt = request.Prompt,
                Scope = request.Scope.ToString(),
                ScopeLabel = request.ScopeLabel,
                StartedAt = startedAt,

                // An edit still under review has not finished from the user's point of
                // view, whatever the process did; the verdict closes it.
                FinishedAt = accepted is null ? null : DateTimeOffset.UtcNow,
                Status = result.Status.ToString(),
                ExitCode = result.ExitCode,
                EditAccepted = accepted,
                Summary = summary,
            },
            CancellationToken.None).ConfigureAwait(false);

        return recorded.IsFailure ? (null, recorded.Error) : (recorded.Value, null);
    }
}

/// <summary>
/// The one adapter that reads and writes the backlog as bytes.
///
/// It sits beside the session rather than in <c>AdoBoardSync.Infrastructure</c>
/// where the rest of the filesystem access lives; moving it there is a follow-up.
/// The port it implements is declared in Core either way, so the seam a test drives
/// is the same one the app runs on.
/// </summary>
public sealed class FileSystemAgentEditFileStore : IAgentEditFileStore
{
    public Result<byte[]> ReadBytes(string path)
    {
        if (!File.Exists(path))
        {
            return Error.NotFound("agent.edit.not_found", $"File not found: {path}.");
        }

        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Error.SourceFailure("agent.edit.unreadable", $"Could not read {path}: {ex.Message}");
        }
    }

    public Result<bool> WriteBytes(string path, byte[] bytes)
    {
        try
        {
            File.WriteAllBytes(path, bytes);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Error.SourceFailure(
                "agent.edit.unwritable",
                $"Could not put {path} back as it was: {ex.Message}");
        }
    }
}
