using AdoBoardSync.Desktop.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AdoBoardSync.Desktop.Views;

/// <summary>
/// The iteration table (ABSD-401). It edits <c>board.config.json</c> and nothing
/// else — every board write still goes through the Plan gate.
/// </summary>
public partial class SprintsView : UserControl
{
    public SprintsView()
    {
        InitializeComponent();
    }

    private MainWindowViewModel? Model => DataContext as MainWindowViewModel;

    private void OnAdd(object? sender, RoutedEventArgs e) => Model?.Sprints.Add();

    /// <summary>
    /// The row comes from the button's own DataContext rather than a selection:
    /// the table has no selected row, and reading one from a shared field would
    /// remove whichever row was clicked last rather than this one.
    /// </summary>
    private void OnRemove(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: SprintRowViewModel row })
        {
            Model?.Sprints.Remove(row);
        }
    }

    // No try/catch: SaveAsync reports its own failures rather than throwing, so an
    // async void handler here has nothing left to let escape.
    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (Model is { } model)
        {
            await model.Sprints.SaveAsync();
        }
    }
}
