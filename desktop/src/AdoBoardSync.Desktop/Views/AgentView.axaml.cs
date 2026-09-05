using AdoBoardSync.Desktop.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AdoBoardSync.Desktop.Views;

/// <summary>
/// The agent-authoring surface (ABSD-703 through ABSD-706).
///
/// Every handler here does one thing and reports its own failure. An
/// <c>async void</c> that let an exception escape would take the process down,
/// and this is the one surface that starts a foreign process.
/// </summary>
public partial class AgentView : UserControl
{
    public AgentView()
    {
        InitializeComponent();
    }

    private MainWindowViewModel? Model => DataContext as MainWindowViewModel;

    private async void OnDiscover(object? sender, RoutedEventArgs e)
    {
        if (Model is not { Agent: { } agent })
        {
            return;
        }

        try
        {
            await agent.DiscoverAsync();
        }
        catch (Exception ex)
        {
            agent.ErrorText = $"Looking for agent CLIs failed: {ex.Message} (agent.probe_failed)";
        }
    }

    /// <summary>
    /// The scope combo carries the option, not the scope, so choosing "the whole
    /// backlog" can also drop the label the previous selection left behind —
    /// otherwise the sentence says "the whole backlog" while the request still
    /// names an Issue.
    /// </summary>
    private void OnScopeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Model is { Agent: { } agent } && ScopeList.SelectedItem is AgentScopeOption option)
        {
            agent.Choose(option);
        }
    }

    private async void OnRun(object? sender, RoutedEventArgs e)
    {
        if (Model is not { Agent: { } agent })
        {
            return;
        }

        try
        {
            await agent.RunAsync();
        }
        catch (Exception ex)
        {
            agent.ErrorText = $"The agent did not run: {ex.Message} (agent.run_failed)";
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Model?.Agent?.Cancel();

    private async void OnAccept(object? sender, RoutedEventArgs e)
    {
        if (Model is not { Agent: { } agent })
        {
            return;
        }

        try
        {
            await agent.AcceptAsync();
        }
        catch (Exception ex)
        {
            agent.ErrorText = $"The edit was not accepted: {ex.Message} (agent.edit.unrecorded)";
        }
    }

    private async void OnReject(object? sender, RoutedEventArgs e)
    {
        if (Model is not { Agent: { } agent })
        {
            return;
        }

        try
        {
            await agent.RejectAsync();
        }
        catch (Exception ex)
        {
            agent.ErrorText = $"The backlog could not be put back: {ex.Message} (agent.edit.unwritable)";
        }
    }

    private void OnPlan(object? sender, RoutedEventArgs e) => Model?.Agent?.RequestPlan();
}
