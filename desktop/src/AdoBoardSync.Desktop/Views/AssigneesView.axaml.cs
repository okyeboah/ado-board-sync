using AdoBoardSync.Desktop.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AdoBoardSync.Desktop.Views;

/// <summary>
/// The assignee table (ABSD-402). It edits <c>board.config.json</c> and nothing
/// else — every board write still goes through the Plan gate.
/// </summary>
public partial class AssigneesView : UserControl
{
    public AssigneesView()
    {
        InitializeComponent();
    }

    private MainWindowViewModel? Model => DataContext as MainWindowViewModel;

    private void OnAdd(object? sender, RoutedEventArgs e) => Model?.Assignees.Add();

    private void OnRemove(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: AssigneeRowViewModel row })
        {
            Model?.Assignees.Remove(row);
        }
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model)
        {
            return;
        }

        try
        {
            await model.Assignees.SaveAsync();
        }
        catch (Exception ex)
        {
            model.Assignees.ErrorText = $"The assignees were not saved: {ex.Message} (config.unsaved)";
        }
    }
}
