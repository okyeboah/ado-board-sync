using AdoBoardSync.Core.Planning;

namespace AdoBoardSync.Desktop.ViewModels;

/// <summary>
/// The confirmation wording — the one sentence a user answers before a write.
/// Split out of the generation half so the gate's question stays beside the
/// counts it restates.
/// </summary>
public sealed partial class PlanViewModel
{
    public string ConfirmQuestion => Plan is null
        ? string.Empty
        : Command switch
        {
            PlanCommand.Import =>
                $"Create {Plan.CreateCount} work item{(Plan.CreateCount == 1 ? string.Empty : "s")} in Azure DevOps?",
            PlanCommand.Resync =>
                $"Update {Plan.UpdateCount} work item{(Plan.UpdateCount == 1 ? string.Empty : "s")} in Azure DevOps?",
            PlanCommand.Sync => ConfirmSyncQuestion(Plan),
            PlanCommand.SetState =>
                $"Move {Plan.UpdateCount} work item{(Plan.UpdateCount == 1 ? string.Empty : "s")} "
                + $"to '{TargetStateText}' in Azure DevOps?",
            PlanCommand.Advance =>
                $"Move {Plan.UpdateCount} Issue{(Plan.UpdateCount == 1 ? string.Empty : "s")} "
                + "from the start state to the working state in Azure DevOps?",
            _ => ConfirmTasksQuestion(Plan)
        };

    private string TargetStateText =>
        string.IsNullOrWhiteSpace(TargetState) ? "the profile's terminal state" : TargetState.Trim();

    private static string ConfirmSyncQuestion(Plan plan)
    {
        var parts = new List<string>();
        if (plan.CreateCount > 0) parts.Add($"create {plan.CreateCount}");

        if (plan.UpdateCount > 0) parts.Add($"update {plan.UpdateCount}");

        if (plan.DeleteCount > 0) parts.Add($"delete {plan.DeleteCount}");

        return parts.Count == 0
            ? "Nothing in the structural reconcile to apply."
            : char.ToUpperInvariant(parts[0][0]) + parts[0][1..]
                                                 + string.Join(string.Empty, parts.Skip(1).Select(p => " and " + p))
                                                 + " — the structural reconcile runs import, resync and the task reconcile in that order. Apply to Azure DevOps?";
    }

    private static string ConfirmTasksQuestion(Plan plan)
    {
        var parts = new List<string>();
        if (plan.CreateCount > 0)
            parts.Add($"create {plan.CreateCount} task{(plan.CreateCount == 1 ? string.Empty : "s")}");

        if (plan.DeleteCount > 0) parts.Add($"delete {plan.DeleteCount}");

        return parts.Count > 0
            ? char.ToUpperInvariant(parts[0][0]) + parts[0][1..]
                                                 + (parts.Count > 1 ? " and " + parts[1] : string.Empty)
                                                 + " in Azure DevOps?"
            : "No Task changes to apply.";
    }

}
