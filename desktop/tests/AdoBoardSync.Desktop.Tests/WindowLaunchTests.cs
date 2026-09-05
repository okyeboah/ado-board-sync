using Avalonia;
using Avalonia.Headless;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;

namespace AdoBoardSync.Desktop.Tests;

/// <summary>
/// The launch gate. A clean build proves nothing about whether the window opens:
/// compiled bindings validate bindings, not resource lookups, so a mistyped
/// StaticResource key compiles with zero warnings. These load the real App and
/// MainWindow headlessly, in both theme variants.
/// </summary>
public class WindowLaunchTests
{
    private static readonly Lock Gate = new();
    private static bool _started;

    /// <summary>
    /// Avalonia allows one application lifetime per process, so every test shares
    /// this one.
    /// </summary>
    private static void EnsurePlatform()
    {
        lock (Gate)
        {
            if (_started)
            {
                return;
            }

            AppBuilder.Configure<App>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true })
                .SetupWithoutStarting();

            _started = true;
        }
    }

    private static void OnUiThread(Action action)
    {
        EnsurePlatform();
        Dispatcher.UIThread.Invoke(action);
    }

    [Fact]
    public void TheApplicationLoadsItsStylesAndThemeResources()
    {
        OnUiThread(() =>
        {
            Assert.NotNull(Application.Current);

            // A missing token is the failure that builds clean and dies at runtime.
            foreach (var key in new[]
                     {
                         "ShellBackgroundBrush", "SidebarBackgroundBrush", "CardBorderBrush",
                         "AppAccentBrush", "AppAccentSoftBrush", "TextPrimaryBrush",
                         "TextSecondaryBrush", "TextMutedBrush", "TextOnAccentBrush",
                         "ValidationErrorBrush", "PlanCreateBrush", "PlanUpdateBrush",
                         "EditorBackgroundBrush", "WindowBackgroundBrush",
                     })
            {
                Assert.True(
                    Application.Current!.Resources.TryGetResource(key, ThemeVariant.Dark, out var value) &&
                    value is not null,
                    $"Theme resource '{key}' did not resolve in the dark variant.");
            }
        });
    }

    /// <summary>
    /// Every resource key any view asks for must exist. A mistyped one leaves the
    /// property at its default and launches looking subtly wrong, with no
    /// exception and no log line, so the keys are scanned out of the XAML and
    /// resolved against the live resource tree.
    /// </summary>
    [Fact]
    public void EveryResourceKeyTheViewsAskForResolves()
    {
        var viewsDirectory = Path.Combine(
            TestKit.RepoPaths.Root, "desktop", "src", "AdoBoardSync.Desktop");

        var keyPattern = new System.Text.RegularExpressions.Regex(
            @"\{(?:Static|Dynamic)Resource\s+([A-Za-z0-9_.]+)\s*\}");

        var referenced = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(viewsDirectory, "*.axaml", SearchOption.AllDirectories))
        {
            foreach (System.Text.RegularExpressions.Match match in keyPattern.Matches(File.ReadAllText(file)))
            {
                referenced.TryAdd(match.Groups[1].Value, Path.GetFileName(file));
            }
        }

        Assert.NotEmpty(referenced);

        OnUiThread(() =>
        {
            var missing = new List<string>();
            foreach (var (key, file) in referenced)
            {
                var resolvesInEitherVariant =
                    (Application.Current!.Resources.TryGetResource(key, ThemeVariant.Dark, out var dark) && dark is not null) ||
                    (Application.Current.Resources.TryGetResource(key, ThemeVariant.Light, out var light) && light is not null);

                if (!resolvesInEitherVariant)
                {
                    missing.Add($"{key} (referenced by {file})");
                }
            }

            Assert.True(missing.Count == 0, "Unresolved resource keys:\n  " + string.Join("\n  ", missing));
        });
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void TheMainWindowLoadsInBothThemeVariants(string variant)
    {
        OnUiThread(() =>
        {
            Application.Current!.RequestedThemeVariant =
                variant == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;

            // InitializeComponent parses the XAML and resolves every
            // StaticResource in it, so a bad key throws here.
            var window = new MainWindow();

            Assert.NotNull(window);
            Assert.Equal("ADO Board Sync", window.Title);
            Assert.NotNull(window.DataContext);
        });
    }

    /// <summary>
    /// The onboarding and Plan panes only instantiate when their section is
    /// selected, so every section is selected here.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void EveryNavSectionRenders(int section)
    {
        OnUiThread(() =>
        {
            var window = new MainWindow();
            var model = (ViewModels.MainWindowViewModel)window.DataContext!;

            window.Show();
            model.CurrentSectionIndex = section;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(section, model.CurrentSectionIndex);
            window.Close();
        });
    }

    /// <summary>The Plan pane must also render with a profile open behind it.</summary>
    [Fact]
    public void ThePlanSectionRendersWithAProfileOpen()
    {
        OnUiThread(() =>
        {
            var window = new MainWindow();
            var model = (ViewModels.MainWindowViewModel)window.DataContext!;

            using var profile = TestKit.TempBoardProfile.Create(
                TestKit.RepoPaths.Fixture("backlog", "standard.md"));

            window.LoadProfile(profile.ConfigPath);
            window.Show();
            model.CurrentSectionIndex = 1;
            Dispatcher.UIThread.RunJobs();

            Assert.True(model.ShowPlan);
            Assert.True(model.HasProfile);
            Assert.False(model.ShowOnboarding);
            window.Close();
        });
    }

    /// <summary>
    /// The pane must open on the rendered preview. It did not: the RadioButton
    /// group raised Click on the HTML button while the view loaded, so a fresh
    /// launch showed markup. Nothing in a view-model test could see that — the
    /// view has to be loaded for the group to coordinate at all.
    /// </summary>
    [Fact]
    public void TheDescriptionPaneOpensOnThePreviewNotTheMarkup()
    {
        OnUiThread(() =>
        {
            var window = new MainWindow();
            var model = (ViewModels.MainWindowViewModel)window.DataContext!;

            using var profile = TestKit.TempBoardProfile.Create(
                TestKit.RepoPaths.Fixture("backlog", "standard.md"));

            window.LoadProfile(profile.ConfigPath);
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.True(model.ShowRenderedPreview, "The pane opened on the markup instead of the preview.");
            Assert.False(model.ShowGeneratedMarkup);
            Assert.Equal("Preview", model.MarkupPaneTitle);

            // And the switch still works in both directions.
            model.ShowGeneratedMarkup = true;
            Dispatcher.UIThread.RunJobs();
            Assert.False(model.ShowRenderedPreview);
            Assert.Equal("Generated HTML", model.MarkupPaneTitle);

            model.ShowRenderedPreview = true;
            Dispatcher.UIThread.RunJobs();
            Assert.False(model.ShowGeneratedMarkup);

            window.Close();
        });
    }

    [Fact]
    public void TheMainWindowSurvivesLoadingARealProfile()
    {
        OnUiThread(() =>
        {
            var window = new MainWindow();

            // Exercises the templates that need data: tree rows, badges, panes.
            using var profile = TestKit.TempBoardProfile.Create(
                TestKit.RepoPaths.Fixture("backlog", "standard.md"));

            window.LoadProfile(profile.ConfigPath);
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.False(((ViewModels.MainWindowViewModel)window.DataContext!).HasError);
            window.Close();
        });
    }
}
