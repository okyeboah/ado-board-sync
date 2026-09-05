using AdoBoardSync.Desktop.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AdoBoardSync.Desktop.Views;

/// <summary>
///     The Plan/Apply surface. Every handler is a thin call into the view model,
///     which owns the gate.
/// </summary>
public partial class PlanView : UserControl
{
    public PlanView()
    {
        InitializeComponent();
    }

    private MainWindowViewModel? Model => DataContext as MainWindowViewModel;


    private async void OnGenerate(object? sender, RoutedEventArgs e)
    {
        if (Model is not { Workspace: { } workspace } model) return;

        try
        {
            await model.BoardPlan.GenerateAsync(workspace);
        }
        catch (Exception ex)
        {
            model.BoardPlan.ErrorText = $"Could not generate the Plan: {ex.Message} (plan.failed)";
        }
    }

    private void OnRequestApply(object? sender, RoutedEventArgs e)
    {
        Model?.BoardPlan.RequestApply(Model.Workspace);
    }

    private void OnCancelApply(object? sender, RoutedEventArgs e)
    {
        Model?.BoardPlan.CancelApply();
    }

    private async void OnConfirmApply(object? sender, RoutedEventArgs e)
    {
        if (Model is not { Workspace: { } workspace } model) return;

        try
        {
            await model.BoardPlan.ApplyConfirmedAsync(workspace);
        }
        catch (Exception ex)
        {
            model.BoardPlan.ErrorText = $"The Apply did not finish: {ex.Message} (apply.failed)";
        }
    }
}
