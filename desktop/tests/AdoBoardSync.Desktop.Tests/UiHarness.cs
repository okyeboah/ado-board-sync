using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Logging;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AdoBoardSync.Desktop.Tests;

/// <summary>
/// The headless UI interaction harness (ABSD-108).
///
/// View-model tests prove what the shell computes. They cannot prove that the XAML
/// asks for it: a binding to a property that does not exist fails silently in
/// Avalonia — the control renders empty and nothing throws — so a renamed property
/// or a mistyped path leaves a green suite and a blank pane. That gap is what this
/// closes. <see cref="BindingFailures" /> captures Avalonia's own binding
/// diagnostics while a window is exercised, and the finders below drive real
/// controls rather than the view model behind them.
///
/// The platform is set up exactly once per process, here, because
/// <c>SetupWithoutStarting</c> throws on a second call — two test classes each
/// bootstrapping their own would fail whichever ran second.
/// </summary>
internal static class UiHarness
{
    private static readonly Lock Gate = new();

    // One dedicated UI thread owns the platform and every job that touches it.
    // SetupWithoutStarting makes the calling thread the dispatcher's owner, so
    // whichever xunit worker happened to boot the platform first used to own it —
    // and every other worker's Dispatcher.Invoke queued a job only that owner
    // could ever run. When the owner moved on, the queued job waited forever: the
    // deadlock a full-suite run died of while no single class ever reproduced it.
    // A thread of our own closes that race for good.
    private static BlockingCollection<Work>? _queue;

    private static bool _started;

    private static void EnsurePlatform()
    {
        lock (Gate)
        {
            if (_started)
            {
                return;
            }

            var queue = new BlockingCollection<Work>();
            var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                AppBuilder.Configure<App>()
                    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true })
                    .SetupWithoutStarting();

                // Await continuations must come back to this thread — a window
                // owns nothing elsewhere, and a continuation on the pool is what
                // made window.Close() an affinity violation.
                SynchronizationContext.SetSynchronizationContext(new UiThreadContext(queue));

                running.TrySetResult();
                foreach (var work in queue.GetConsumingEnumerable())
                {
                    try
                    {
                        work.Body();
                        work.Done.TrySetResult();
                    }
                    catch (Exception ex)
                    {
                        work.Done.TrySetException(ex);
                    }
                }
            })
            {
                IsBackground = true,
                Name = "ui-harness",
            };
            thread.Start();
            running.Task.Wait(TimeSpan.FromSeconds(30));

            _queue = queue;
            _started = true;
        }
    }

    private static Work Post(Action body)
    {
        EnsurePlatform();
        var work = new Work(body);
        _queue!.Add(work);
        return work;
    }

    /// <summary>Runs on the UI thread, and waits for it there.</summary>
    public static void OnUiThread(Action action)
    {
        Post(action).Done.Task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Runs an awaited interaction on the UI thread, end to end.
    ///
    /// The body is queued whole; at its first await it yields the thread and its
    /// continuations are queued back onto it by the context, so a suspended test
    /// never holds anything up — the next queued job, this or another test's, runs
    /// while it waits, exactly as a window's message loop would interleave them.
    /// The caller blocks on its own completion only.
    /// </summary>
    public static void AwaitOnUiThread(Func<Task> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() => _ = Drive(action, done));

        if (!done.Task.Wait(TimeSpan.FromSeconds(30)))
        {
            Assert.Fail("The awaited interaction did not complete on the UI thread.");
        }

        // A faulted interaction must fail the test that caused it, not time out.
        done.Task.GetAwaiter().GetResult();
    }

    private static async Task Drive(Func<Task> action, TaskCompletionSource done)
    {
        try
        {
            await action().ConfigureAwait(true);
            done.TrySetResult();
        }
        catch (Exception ex)
        {
            done.TrySetException(ex);
        }
    }

    /// <summary>Puts every await continuation back on the UI thread it left.</summary>
    private sealed class UiThreadContext(BlockingCollection<Work> queue) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) =>
            queue.Add(new Work(() => callback(state)));

        public override void Send(SendOrPostCallback callback, object? state)
        {
            if (SynchronizationContext.Current is UiThreadContext)
            {
                callback(state);
                return;
            }

            var work = new Work(() => callback(state));
            queue.Add(work);
            work.Done.Task.GetAwaiter().GetResult();
        }
    }

    private sealed class Work(Action body)
    {
        public Action Body { get; } = body;

        public TaskCompletionSource Done { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// Waits for a condition the shell reaches through a fire-and-forget task —
    /// profile registration and the timeline load are both <c>_ =</c> calls in
    /// <c>Adopt</c> — by giving the UI thread's queue time to run them. The
    /// sync-blocking form is for callers outside the UI thread; inside an
    /// <see cref="AwaitOnUiThread" /> body it would stop the very queue that has
    /// to run the condition into being, so await <see cref="WaitUntilAsync" /> there.
    /// </summary>
    public static void WaitUntil(Func<bool> condition, string because)
    {
        var deadline = Environment.TickCount64 + 30_000;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                Assert.True(condition(), because);
            }

            Thread.Sleep(10);
        }
    }

    /// <summary>The awaitable form of <see cref="WaitUntil" />, for use inside an
    /// <see cref="AwaitOnUiThread" /> body: each wait yields the thread, so the
    /// queue can run whatever the condition is waiting for.</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        var deadline = Environment.TickCount64 + 30_000;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                Assert.True(condition(), because);
            }

            await Task.Delay(10).ConfigureAwait(true);
        }
    }

    /// <summary>Lets queued layout, binding and rendering work run to completion.</summary>
    public static void Pump()
    {
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    // ------------------------------------------------------------- finding

    /// <summary>
    /// Every control of a type below <paramref name="root" />, including the ones
    /// inside templates. The logical tree is searched as well as the visual one:
    /// an item in a not-yet-realised <c>ItemsControl</c> panel is logical-only.
    /// </summary>
    public static List<T> All<T>(Visual root) where T : Control
    {
        var found = new List<T>();

        foreach (var control in root.GetVisualDescendants().OfType<T>())
        {
            found.Add(control);
        }

        if (root is ILogical logical)
        {
            foreach (var control in logical.GetLogicalDescendants().OfType<T>())
            {
                if (!found.Contains(control))
                {
                    found.Add(control);
                }
            }
        }

        return found;
    }

    /// <summary>The one control of a type whose content matches, named for the assertion message.</summary>
    public static T Only<T>(Visual root, Func<T, bool> matching, string described) where T : Control
    {
        var matches = All<T>(root).Where(matching).ToList();

        Assert.True(
            matches.Count == 1,
            $"Expected exactly one {typeof(T).Name} matching {described}, found {matches.Count}.");

        return matches[0];
    }

    /// <summary>
    /// Whether a control is actually on screen.
    ///
    /// Not <c>IsEffectivelyVisible</c>: a pane whose <c>IsVisible</c> is false is
    /// never attached to the visual tree, so the controls inside it are reachable
    /// only logically and each reports itself effectively visible — the property is
    /// only meaningful once something is attached. Walking the logical ancestors
    /// gives the answer for the collapsed panes too, which is the whole difficulty,
    /// since every section of the shell is in the tree at once.
    /// </summary>
    public static bool IsShown(Control control)
    {
        for (ILogical? node = control; node is not null; node = node.LogicalParent)
        {
            if (node is Visual { IsVisible: false })
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A visible button by the text on its face, as a user would name it. Visibility
    /// is part of the match, not an afterthought: two panes each offering a "Save"
    /// button is normal here, and a finder that ignored that would report an
    /// ambiguity on a perfectly correct shell.
    /// </summary>
    public static Button Button(Visual root, string content) =>
        Only<Button>(
            root,
            b => IsShown(b) && b.Content is string text && string.Equals(text, content, StringComparison.Ordinal),
            $"the visible caption \"{content}\"");

    /// <summary>Whether any visible control below the root shows this text.</summary>
    public static bool ShowsText(Visual root, string text) =>
        All<TextBlock>(root).Any(block =>
            IsShown(block)
            && block.Text is { } shown
            && shown.Contains(text, StringComparison.Ordinal));

    // ---------------------------------------------------------- interacting

    /// <summary>Presses a button the way the shell's own click handler is reached.</summary>
    public static void Click(Button button)
    {
        Assert.True(IsShown(button), "The button is not visible, so a user could not press it.");
        Assert.True(button.IsEnabled, "The button is disabled, so a user could not press it.");

        // The Click routed event, which is what both the XAML handlers and any
        // bound command hang off. A synthesised pointer press would be closer to a
        // real user but depends on hit-testing that headless layout may not have
        // settled, which makes it a source of flakes rather than of confidence.
        button.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Pump();
    }

    /// <summary>Types into a text box as an edit, so two-way bindings fire.</summary>
    public static void Type(TextBox box, string text)
    {
        box.Text = text;
        Pump();
    }

    // ------------------------------------------------------ binding failures

    /// <summary>
    /// Runs an action with Avalonia's binding diagnostics captured, and returns
    /// whatever it complained about. An empty list is the assertion worth making:
    /// it means every path the XAML asked for actually resolved.
    /// </summary>
    public static IReadOnlyList<string> BindingFailures(Action action)
    {
        var sink = new CapturingSink(Logger.Sink);
        Logger.Sink = sink;
        try
        {
            action();
            Pump();
        }
        finally
        {
            Logger.Sink = sink.Previous;
        }

        return sink.Messages;
    }

    /// <summary>
    /// Records binding warnings and errors, and forwards everything to whatever
    /// sink was installed before — replacing a sink outright would silence the
    /// diagnostics of anything else running at the same time.
    /// </summary>
    private sealed class CapturingSink(ILogSink? previous) : ILogSink
    {
        private readonly Lock _gate = new();
        private readonly List<string> _messages = [];

        public ILogSink? Previous { get; } = previous;

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_gate)
                {
                    return [.. _messages];
                }
            }
        }

        public bool IsEnabled(LogEventLevel level, string area) =>
            level >= LogEventLevel.Warning || Previous?.IsEnabled(level, area) == true;

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
        {
            Record(level, area, source, messageTemplate, []);
            Previous?.Log(level, area, source, messageTemplate);
        }

        public void Log(
            LogEventLevel level, string area, object? source, string messageTemplate,
            params object?[] propertyValues)
        {
            Record(level, area, source, messageTemplate, propertyValues);
            Previous?.Log(level, area, source, messageTemplate, propertyValues);
        }

        private void Record(
            LogEventLevel level, string area, object? source, string messageTemplate, object?[] values)
        {
            if (level < LogEventLevel.Warning || !string.Equals(area, LogArea.Binding, StringComparison.Ordinal))
            {
                return;
            }

            var rendered = messageTemplate;
            foreach (var value in values)
            {
                var placeholder = rendered.IndexOf('{');
                var end = placeholder < 0 ? -1 : rendered.IndexOf('}', placeholder);
                if (end < 0)
                {
                    break;
                }

                rendered = rendered[..placeholder] + value + rendered[(end + 1)..];
            }

            lock (_gate)
            {
                _messages.Add($"{level} [{area}] {source?.GetType().Name}: {rendered}");
            }
        }
    }
}
