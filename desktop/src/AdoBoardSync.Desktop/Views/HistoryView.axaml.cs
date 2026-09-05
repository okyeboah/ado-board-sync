using AdoBoardSync.Desktop.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AdoBoardSync.Desktop.Views;

/// <summary>
/// The Apply timeline (ABSD-508). Read-only, and local: it reports what this
/// machine did, never what the board currently says.
/// </summary>
public partial class HistoryView : UserControl
{
    public HistoryView()
    {
        InitializeComponent();
    }

    private MainWindowViewModel? Model => DataContext as MainWindowViewModel;

    private async void OnRefresh(object? sender, RoutedEventArgs e)
    {
        if (Model is not { Workspace: { } workspace, History: { } history })
        {
            return;
        }

        try
        {
            await history.LoadAsync(workspace);
        }
        catch (Exception ex)
        {
            history.ErrorText = $"The history did not load: {ex.Message} (history.unreadable)";
        }
    }

    private async void OnToggle(object? sender, RoutedEventArgs e)
    {
        if (Model is not { History: { } history }
            || sender is not Control { DataContext: OperationRunViewModel run })
        {
            return;
        }

        try
        {
            await history.ToggleAsync(run);
        }
        catch (Exception ex)
        {
            history.ErrorText = $"That run's rows did not load: {ex.Message} (history.unreadable)";
        }
    }
}
