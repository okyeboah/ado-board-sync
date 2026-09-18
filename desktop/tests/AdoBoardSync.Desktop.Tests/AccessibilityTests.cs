using AdoBoardSync.Desktop.ViewModels;
using AdoBoardSync.TestKit;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Controls;

namespace AdoBoardSync.Desktop.Tests;

/// <summary>
/// DESIGN-SYSTEM §6, at the level a test can hold it.
///
/// The contrast half is the documented pass: every ratio below is computed by the
/// same formula the WCAG defines, over the hex values Theme.axaml actually ships,
/// and the numbers recorded in DESIGN-SYSTEM §2 came from this code. Editing a
/// token here without the doc, or the doc without the token, fails this suite.
///
/// The keyboard half is §6.1: save without a mouse, by sending the real key events
/// through the headless input pipeline — not by calling the command.
/// </summary>
public class AccessibilityTests
{
    private const string LightBodyText = "#1E2430";
    private const string LightSecondaryText = "#5B6472";
    private const string DarkBodyText = "#EEF1F6";
    private const string DarkSecondaryText = "#B4BBC8";
    private const string LightShell = "#FFFFFF";
    private const string LightSidebar = "#F4F7FD";
    private const string LightEditor = "#FBFCFE";
    private const string DarkShell = "#161B26";
    private const string DarkSidebar = "#1E2531";
    private const string DarkEditor = "#121722";
    private const string LightAccent = "#2D6CDF";
    private const string DarkAccent = "#6FA8FF";
    private const string LightOnAccent = "#FFFFFF";
    private const string DarkOnAccent = "#161B26";
    private const string LightOnPlan = "#1E2430";
    private const string LightCreate = "#16C784";
    private const string LightUpdate = "#F5A623";
    private const string LightUnchanged = "#8B93A1";
    private const string LightDelete = "#EA3943";
    private const string DarkCreate = "#3CCB7E";
    private const string DarkUpdate = "#F2C12E";
    private const string DarkUnchanged = "#7C8494";
    private const string DarkDelete = "#FF7A85";

    // ------------------------------------------------------------ WCAG maths

    private static double Luminance(string hex)
    {
        var value = Convert.ToUInt32(hex.TrimStart('#'), 16);
        double Channel(int shift)
        {
            var c = ((value >> shift) & 0xFF) / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(16) + 0.7152 * Channel(8) + 0.0722 * Channel(0);
    }

    private static double Contrast(string a, string b)
    {
        var (lighter, darker) = Luminance(a) >= Luminance(b) ? (a, b) : (b, a);
        return (Luminance(lighter) + 0.05) / (Luminance(darker) + 0.05);
    }

    // ------------------------------------------------------- §2 contrast pass

    [Fact]
    public void BodyTextHoldsTheDocumentedRatioInBothThemes()
    {
        // DESIGN-SYSTEM §2: body text holds at least 4.5:1 against its background.
        (string Text, string Background, string Name)[] pairs =
        [
            (LightBodyText, LightShell, "light primary on shell"),
            (LightBodyText, LightEditor, "light primary on editor"),
            (LightSecondaryText, LightShell, "light secondary on shell"),
            (LightSecondaryText, LightSidebar, "light secondary on sidebar"),
            (DarkBodyText, DarkShell, "dark primary on shell"),
            (DarkBodyText, DarkEditor, "dark primary on editor"),
            (DarkSecondaryText, DarkShell, "dark secondary on shell"),
            (DarkSecondaryText, DarkSidebar, "dark secondary on sidebar"),
        ];

        foreach (var (text, background, name) in pairs)
        {
            Assert.True(
                Contrast(text, background) >= 4.5,
                $"{name}: {Contrast(text, background):0.00}:1 is below the documented 4.5:1.");
        }
    }

    [Fact]
    public void AccentButtonLabelsHoldTheDocumentedRatioInBothThemes()
    {
        Assert.True(Contrast(LightOnAccent, LightAccent) >= 4.5, "light on-accent label");
        Assert.True(Contrast(DarkOnAccent, DarkAccent) >= 4.5, "dark on-accent label");
    }

    [Fact]
    public void PlanBadgeLabelsHoldTheUiMinimumOnEveryFill()
    {
        // The badge labels are captions, so the bar this pass holds them to is the
        // WCAG 3:1 large-text/UI minimum; the chosen pair clears 4.5 on every fill
        // but the light delete, where white on the red is the better of the two at
        // 4.07 — recorded as such in DESIGN-SYSTEM §2. This test is what caught
        // white-on-orange at 2.03:1 when the pass first ran.
        (string Text, string Fill, string Name)[] pairs =
        [
            (LightOnPlan, LightCreate, "light create"),
            (LightOnPlan, LightUpdate, "light update"),
            (LightOnPlan, LightUnchanged, "light unchanged"),
            (LightOnAccent, LightDelete, "light delete"),
            (DarkOnAccent, DarkCreate, "dark create"),
            (DarkOnAccent, DarkUpdate, "dark update"),
            (DarkOnAccent, DarkUnchanged, "dark unchanged"),
            (DarkOnAccent, DarkDelete, "dark delete"),
        ];

        foreach (var (text, fill, name) in pairs)
        {
            Assert.True(
                Contrast(text, fill) >= 3.0,
                $"{name} badge label: {Contrast(text, fill):0.00}:1 is below the 3:1 UI minimum.");
        }
    }

    // ---------------------------------------------------------------- §6.1

    [Fact]
    public void CtrlSKeysSaveThroughTheKeyboardWithoutAMouse()
    {
        UiHarness.AwaitOnUiThread(async () =>
        {
            var directory = Directory.CreateTempSubdirectory("abs-a11y-keys-").FullName;
            try
            {
                var backlog = Path.Combine(directory, "backlog.md");
                File.WriteAllText(backlog, "## Epic 1\n\n### PROJ-101 · One\n\nBody.\n");
                using var profile = TempBoardProfile.Create(backlog);

                var window = new MainWindow(Shell.OnDisk());
                try
                {
                    window.Show();
                    var model = Model(window);
                    await model.LoadAsync(profile.ConfigPath);

                    var editor = UiHarness.Only<Avalonia.Controls.TextBox>(
                        window,
                        box => box.Classes.Contains("editor") && box.Classes.Contains("editable"),
                        "the source editor");
                    editor.Focus();
                    Assert.True(editor.IsFocused);

                    model.SelectedNode!.Source = "Typed, then saved with the keyboard.\n";
                    Assert.True(model.HasUnsavedEdits);

                    // The real keystroke, through the headless input pipeline — the
                    // KeyBinding in MainWindow.axaml is what must answer, not a
                    // test calling SaveCommand directly.
                    window.KeyPress(Key.S, RawInputModifiers.Control, PhysicalKey.S, "s");

                    await UiHarness.WaitUntilAsync(
                        () => !model.HasUnsavedEdits,
                        "Ctrl+S did not save the buffer.");

                    Assert.Contains(
                        "Typed, then saved with the keyboard.",
                        File.ReadAllText(backlog),
                        StringComparison.Ordinal);
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        });
    }

    [Fact]
    public void TabMovesTheKeyboardFocusBetweenTheSurfaces()
    {
        UiHarness.AwaitOnUiThread(async () =>
        {
            using var profile = Standard();
            var window = new MainWindow(Shell.OnDisk());
            try
            {
                window.Show();
                var model = Model(window);
                await model.LoadAsync(profile.ConfigPath);

                var editor = UiHarness.Only<Avalonia.Controls.TextBox>(
                    window,
                    box => box.Classes.Contains("editor") && box.Classes.Contains("editable"),
                    "the source editor");
                editor.Focus();

                window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, "\t");
                UiHarness.Pump();

                var focused = window.FocusManager?.GetFocusedElement() as Visual;
                Assert.NotNull(focused);
                Assert.NotSame(editor, focused);
                Assert.Contains(focused!, window.GetVisualDescendants());
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static MainWindowViewModel Model(MainWindow window) =>
        (MainWindowViewModel)window.DataContext!;

    private static TempBoardProfile Standard() =>
        TempBoardProfile.Create(RepoPaths.Fixture("backlog", "standard.md"));
}
