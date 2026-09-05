using AdoBoardSync.Desktop.Composition;
using AdoBoardSync.Desktop.ViewModels;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;

namespace AdoBoardSync.Desktop;

public partial class App : Application
{
    /// <summary>
    /// The application's one container (ABSD-106). Built here because this is the
    /// only place that owns the process lifetime; the headless launch gate builds
    /// its own, so nothing static is shared between test runs.
    /// </summary>
    public ServiceProvider Services { get; } = AppServices.Build();

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow(Services.GetRequiredService<MainWindowViewModel>());

            // An optional profile path, as an IDE run configuration or
            // `dotnet run -- <config>` passes it.
            if (desktop.Args is [var configPath, ..] && !string.IsNullOrWhiteSpace(configPath))
            {
                window.LoadProfile(configPath);
            }

            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
