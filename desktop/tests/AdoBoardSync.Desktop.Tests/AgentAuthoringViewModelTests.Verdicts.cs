using System.Text;

using AdoBoardSync.Core.Agents;

using AdoBoardSync.Core.Backlog;

using AdoBoardSync.Core.Configuration;

using AdoBoardSync.Core.Operations;

using AdoBoardSync.Core.Results;

using AdoBoardSync.Desktop.Services;

using AdoBoardSync.Desktop.ViewModels;

namespace AdoBoardSync.Desktop.Tests;

/// <summary>Agent-authoring tests for the verdict/accept/reject half of the session.</summary>
public sealed partial class AgentAuthoringViewModelTests
{
    [Fact]
    public async Task ARunThatChangedNothingIsSaidToHaveChangedNothing()
    {
        var subject = await ReadyAsync(agentWrites: Original);

        await subject.Model.RunAsync();

        Assert.False(subject.Model.HasReview);
        Assert.Empty(subject.Model.DiffLines);
        Assert.Contains("no change", subject.Model.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ARunThatFailedRestoresTheFileAndOffersNoDiff()
    {
        var subject = await ReadyAsync(status: AgentRunStatus.Failed);

        await subject.Model.RunAsync();

        Assert.False(subject.Model.HasReview);
        Assert.Equal(Original, subject.Files.Text(subject.Workspace.BacklogPath));
    }

    [Fact]
    public async Task AnUnparseableEditIsRefusedWithItsReasonRatherThanShown()
    {
        // A diff of a backlog that no longer parses is a diff of something this app
        // cannot plan from, and accepting it would break the profile.
        var subject = await ReadyAsync(agentWrites: "not a backlog at all\n");

        await subject.Model.RunAsync();

        Assert.False(subject.Model.HasReview);
        Assert.True(subject.Model.HasError);
        Assert.Equal(Original, subject.Files.Text(subject.Workspace.BacklogPath));
    }

    // ---------------------------------------------------------- the verdict

    [Fact]
    public async Task AcceptingKeepsTheEditRecordsTheVerdictAndHandsOverTheParse()
    {
        var subject = await ReadyAsync();
        await subject.Model.RunAsync();

        AgentEditReview? handed = null;
        subject.Model.EditAccepted = review => handed = review;

        await subject.Model.AcceptAsync();

        Assert.Equal(Edited, subject.Files.Text(subject.Workspace.BacklogPath));
        Assert.Equal([(1L, true)], subject.History.Verdicts);
        Assert.NotNull(handed);
        Assert.False(subject.Model.HasReview);
        Assert.Empty(subject.Model.DiffLines);
    }

    [Fact]
    public async Task RejectingPutsTheFileBackAndRecordsTheVerdict()
    {
        var subject = await ReadyAsync();
        await subject.Model.RunAsync();

        await subject.Model.RejectAsync();

        Assert.Equal(Original, subject.Files.Text(subject.Workspace.BacklogPath));
        Assert.Equal([(1L, false)], subject.History.Verdicts);
        Assert.False(subject.Model.HasReview);
        Assert.Contains("exactly as it was", subject.Model.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedRestoreIsReportedRatherThanClaimingTheBacklogIsBack()
    {
        var subject = await ReadyAsync();
        await subject.Model.RunAsync();
        subject.Files.WriteError = Error.SourceFailure("agent.edit.unwritable", "read-only volume");

        await subject.Model.RejectAsync();

        Assert.True(subject.Model.HasError);
        Assert.Contains("agent.edit.unwritable", subject.Model.ErrorText);
        Assert.Contains("could not be put back", subject.Model.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AVerdictWithNothingUnderReviewDoesNothing()
    {
        var subject = await ReadyAsync();

        await subject.Model.AcceptAsync();
        await subject.Model.RejectAsync();

        Assert.Empty(subject.History.Verdicts);
        Assert.Equal(Original, subject.Files.Text(subject.Workspace.BacklogPath));
    }

    // ------------------------------------------------------ the Plan handoff

    [Fact]
    public async Task PlanningIsOfferedOnlyAfterAnEditWasAccepted()
    {
        var subject = await ReadyAsync();
        Assert.False(subject.Model.CanPlan);

        await subject.Model.RunAsync();
        Assert.False(subject.Model.CanPlan);

        await subject.Model.AcceptAsync();
        Assert.True(subject.Model.CanPlan);
    }

    [Fact]
    public async Task AskingForAPlanOnlyAsksTheShellToOpenIt()
    {
        // ABSD-705. The request carries no plan, no approval and no board write —
        // an agent's involvement removes no step from the Plan/Apply gate.
        var subject = await ReadyAsync();
        await subject.Model.RunAsync();
        await subject.Model.AcceptAsync();

        var asked = 0;
        subject.Model.PlanRequested = () => asked++;

        subject.Model.RequestPlan();

        Assert.Equal(1, asked);
    }

    [Fact]
    public async Task AskingForAPlanBeforeAnAcceptAsksForNothing()
    {
        var subject = await ReadyAsync();
        await subject.Model.RunAsync();

        var asked = 0;
        subject.Model.PlanRequested = () => asked++;

        subject.Model.RequestPlan();

        Assert.Equal(0, asked);
    }

    [Fact]
    public async Task StartingASecondRunClearsTheFirstRunsOutputAndVerdict()
    {
        // A second run that inherited the first one's output would be read as its
        // own, and the accepted-edit flag would still be offering a stale Plan.
        var subject = await ReadyAsync();
        await subject.Model.RunAsync();
        await subject.Model.AcceptAsync();
        Assert.True(subject.Model.CanPlan);

        subject.Model.Prompt = "Do something else.";
        await subject.Model.RunAsync();

        Assert.Equal("working…", Assert.Single(await OutputAsync(subject.Model)));
        Assert.False(subject.Model.HasAcceptedEdit);
    }
}
