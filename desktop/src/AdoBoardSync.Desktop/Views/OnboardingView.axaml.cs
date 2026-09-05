using AdoBoardSync.Desktop.Services;
using AdoBoardSync.Desktop.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;

namespace AdoBoardSync.Desktop.Views;

/// <summary>
/// The first-run choice. Pickers live here so the view model stays testable
/// without a storage provider.
/// </summary>
public partial class OnboardingView : UserControl
{
    public OnboardingView() => InitializeComponent();

    /// <summary>Raised when the user picks the "open an existing config" route.</summary>
    public event EventHandler? OpenExistingRequested;

    /// <summary>Raised with the opened profile when the form route succeeds.</summary>
    public event EventHandler<BacklogWorkspace>? ProfileOpened;

    private MainWindowViewModel? Model => DataContext as MainWindowViewModel;

    private void OnOpenExisting(object? sender, RoutedEventArgs e) =>
        OpenExistingRequested?.Invoke(this, EventArgs.Empty);

    private async void OnChooseBacklog(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model)
        {
            return;
        }

        var path = await PickFileAsync(
            "Choose the backlog Markdown file",
            new FilePickerFileType("Markdown") { Patterns = ["*.md", "*.markdown"] });

        if (path is not null)
        {
            model.Onboarding.BacklogPath = path;
            model.Onboarding.SuggestSavePath();
        }
    }

    private async void OnChooseSavePath(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model)
        {
            return;
        }

        try
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage is null)
            {
                return;
            }

            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save the profile",
                SuggestedFileName = "board.config.json",
                DefaultExtension = "json",
                FileTypeChoices = [new FilePickerFileType("Board config") { Patterns = ["*.json"] }],
            });

            if (file?.TryGetLocalPath() is { } path)
            {
                model.Onboarding.SavePath = path;
            }
        }
        catch (Exception ex)
        {
            model.Onboarding.ErrorText = $"Could not choose a save location: {ex.Message} (save.failed)";
        }
    }

    private void OnCreateProfile(object? sender, RoutedEventArgs e) => _ = CreateProfileAsync();

    private async Task CreateProfileAsync()
    {
        if (Model is not { } model)
        {
            return;
        }

        var created = await model.Onboarding.CreateProfileAsync();
        if (created.IsSuccess)
        {
            ProfileOpened?.Invoke(this, created.Value);
        }
    }

    private async Task<string?> PickFileAsync(string title, FilePickerFileType type)
    {
        try
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage is null)
            {
                return null;
            }

            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                FileTypeFilter = [type],
            });

            return files.Count > 0 ? files[0].TryGetLocalPath() : null;
        }
        catch (Exception ex)
        {
            if (Model is { } model)
            {
                model.Onboarding.ErrorText = $"Could not open that file: {ex.Message} (open.failed)";
            }

            return null;
        }
    }
}
