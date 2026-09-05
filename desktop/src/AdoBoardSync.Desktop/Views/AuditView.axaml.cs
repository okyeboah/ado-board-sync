using AdoBoardSync.Desktop.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AdoBoardSync.Desktop.Views;

/// <summary>
///     The Audit surface. Every handler is a thin call into the view model, and none
///     of them can reach a write: the view model holds no write path at all
///     (ABSD-306), and the close-children action asks the shell to open the Plan
///     gate rather than applying anything itself.
/// </summary>
public partial class AuditView : UserControl
{
    public AuditView()
    {
        InitializeComponent();
    }

    private MainWindowViewModel? Model => DataContext as MainWindowViewModel;

    private async void OnRunAudit(object? sender, RoutedEventArgs e)
    {
        if (Model is not { Workspace: { } workspace } model)
        {
            return;
        }

        try
        {
            await model.Audit.RunAsync(workspace);
        }
        catch (Exception ex)
        {
            model.Audit.ErrorText = $"The audit did not finish: {ex.Message} (audit.failed)";
        }
    }

    private void OnCloseChildren(object? sender, RoutedEventArgs e)
    {
        Model?.Audit.RequestCloseChildren();
    }
}
