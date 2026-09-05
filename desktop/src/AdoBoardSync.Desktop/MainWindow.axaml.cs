using AdoBoardSync.Desktop.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace AdoBoardSync.Desktop;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    /// <summary>
    /// The parameterless constructor the XAML designer and the headless launch gate
    /// need. It builds its own container rather than reaching for a static one, so
    /// two windows never share a profile loader.
    /// </summary>
    public MainWindow()
        : this(Composition.AppServices.Build().GetRequiredService<MainWindowViewModel>())
    {
    }

    public MainWindow(MainWindowViewModel viewModel)
    {
        _viewModel = viewModel;

        // Before InitializeComponent, not after. Every binding in the tree attaches
        // as the XAML is loaded, so a DataContext assigned afterwards means each one
        // first resolves against null and logs a failure before re-evaluating. The
        // window looked right either way; the log was full of warnings that made a
        // real binding mistake impossible to spot (ABSD-108).
        DataContext = _viewModel;
        InitializeComponent();

        // Onboarding owns the form, not the shell, so it reports back instead of
        // reaching into the window's state.
        OnboardingPane.OpenExistingRequested += (_, _) => _ = PickProfileAsync();
        OnboardingPane.ProfileOpened += (_, workspace) => _viewModel.Adopt(workspace);

        // The staleness poll (ABSD-504). The shell decides what stale means; this
        // decides when to ask. Two triggers, because neither alone is enough: a
        // window left open all afternoon needs the timer, and a user who tabs away
        // to edit the backlog in another editor expects to be told the moment they
        // come back, not up to thirty seconds later.
        _staleness = new DispatcherTimer(
            TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) => CheckForExternalChange());

        Activated += (_, _) => CheckForExternalChange();
    }

    private readonly DispatcherTimer _staleness;

    private void CheckForExternalChange() => _ = _viewModel.CheckForExternalChangeAsync();

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _staleness.Start();
    }

    /// <summary>
    /// Stops the poll with the window. A timer left running holds the view model
    /// alive and keeps reading a file nobody is looking at.
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        _staleness.Stop();
        base.OnClosed(e);
    }

    /// <summary>Loads a Board profile by path, used for command-line startup.</summary>
    public void LoadProfile(string configPath) => _ = _viewModel.LoadAsync(configPath);

    private void OnOpenClick(object? sender, RoutedEventArgs e) => _ = PickProfileAsync();

    private void OnReloadClick(object? sender, RoutedEventArgs e) => _ = _viewModel.ReloadAsync();

    private void OnExportCsvClick(object? sender, RoutedEventArgs e) => _ = PickCsvDestinationAsync();

    /// <summary>
    ///     Switches profiles (ABSD-502).
    ///
    ///     The guard is not decoration. The combo's selection is bound to the
    ///     switcher's own <c>ActiveProfile</c>, so every programmatic change —
    ///     opening a profile, adopting a saved one, loading the registry at startup —
    ///     raises this too. Without the comparison each of those would re-activate
    ///     the profile that is already open, rewriting the registry file for nothing.
    /// </summary>
    private void OnProfileChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_viewModel.Profiles is not { } profiles
            || ProfileSwitcher.SelectedItem is not ProfileRowViewModel row
            || ReferenceEquals(row, profiles.ActiveProfile))
        {
            return;
        }

        _ = profiles.SetActiveAsync(row.ConfigPath);
    }

    /// <summary>
    ///     Un-registers the open profile (ABSD-502). It forgets the entry and
    ///     nothing else — no file on disk is touched, which is why this needs no
    ///     confirmation: the worst outcome is re-opening the same
    ///     <c>board.config.json</c> from the file picker.
    /// </summary>
    private void OnForgetProfile(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.Profiles is { ActiveProfile: { } active } profiles)
        {
            _ = profiles.RemoveAsync(active.ConfigPath);
        }
    }

    /// <summary>
    /// The picker lives in the view; the view model takes a path, so it stays
    /// testable without a storage provider.
    /// </summary>
    private async Task PickCsvDestinationAsync()
    {
        try
        {
            var storage = GetTopLevel(this)?.StorageProvider;
            if (storage is null)
            {
                return;
            }

            // Offered at the profile's own csv_file, which is where the CLI writes
            // it and therefore where a user expects to find it (ABSD-207).
            var suggested = _viewModel.SuggestedCsvPath;
            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Write the import CSV",
                SuggestedFileName = Path.GetFileName(suggested),
                DefaultExtension = "csv",
                FileTypeChoices =
                [
                    new FilePickerFileType("Import CSV") { Patterns = ["*.csv"] },
                ],
            });

            if (file?.TryGetLocalPath() is { } path)
            {
                // The picker confirms its own overwrite on every platform Avalonia
                // targets; asking again here would be a second prompt for one
                // decision the user already made.
                await _viewModel.ExportCsvToAsync(path);
            }
        }
        catch (Exception ex)
        {
            _viewModel.ErrorText = $"Could not write the CSV: {ex.Message} (csv.unwritten)";
        }
    }

    /// <summary>
    /// The picker lives in the view; the view model takes a path, so it stays
    /// testable without a storage provider.
    /// </summary>
    private async Task PickProfileAsync()
    {
        try
        {
            var storage = GetTopLevel(this)?.StorageProvider;
            if (storage is null)
            {
                return;
            }

            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open board.config.json",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("Board config") { Patterns = ["*.json"] },
                ],
            });

            if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            {
                // First run: a bad config file should be explained on the
                // onboarding screen, where the file was chosen, not by replacing
                // it with an error page. With a profile open, the shell handles it.
                if (_viewModel.HasProfile)
                {
                    await _viewModel.LoadAsync(path);
                }
                else
                {
                    await _viewModel.OpenFromOnboardingAsync(path);
                }
            }
        }
        catch (Exception ex)
        {
            _viewModel.ErrorText = $"Could not open that file: {ex.Message} (open.failed)";
        }
    }
}
