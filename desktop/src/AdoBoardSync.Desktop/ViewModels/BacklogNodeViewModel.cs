using System.Collections.ObjectModel;
using AdoBoardSync.Core;
using AdoBoardSync.Core.Backlog;
using AdoBoardSync.Core.Markdown;
using AdoBoardSync.Desktop.Preview;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdoBoardSync.Desktop.ViewModels;

/// <summary>One markup problem, scoped the way the CLI's <c>check-html</c> scopes it.</summary>
public sealed record MarkupProblem(string Scope, string Message)
{
    public string Display => $"{Scope}: {Message}";
}

/// <summary>One Task bullet, shown with the title the CLI would send.</summary>
public sealed record TaskLine(string Title, string Html)
{
}

/// <summary>
///     One Epic or Issue in the backlog tree. The HTML and the problem list come from
///     the same Core functions the CLI calls, so this shows what <c>import</c> sends.
///     The problems come from <see cref="BacklogMarkupAudit" />, the same audit whose
///     total gates Apply — a row here and the gate can never disagree.
/// </summary>
/// <summary>
///     The node is also the editing unit. <see cref="Source" /> is the editor buffer
///     for the item's description block; every derived view of it — preview, generated
///     HTML, task list, markup problems — is recomputed from the buffer as it types,
///     so what the user sees is always what this text would send. <see cref="Item" />
///     stays the parsed original: its identity and line ranges are what saving splices
///     back into the file. The tree itself is only rebuilt on load and save, so
///     typing never moves the caret or the selection.
/// </summary>
public sealed partial class BacklogNodeViewModel : ObservableObject
{
    private readonly string _originalSource;
    private string _source;

    public BacklogNodeViewModel(BacklogItem item)
    {
        Item = item;
        _originalSource = string.Join(Environment.NewLine, item.DescriptionLines);
        _source = _originalSource;
        Recompute();
    }

    public BacklogItem Item { get; }

    public ObservableCollection<BacklogNodeViewModel> Children { get; } = [];

    public bool IsEpic => Item.Level == BacklogLevel.Epic;

    public string Badge => IsEpic ? "EPIC" : Item.Code ?? "ISSUE";

    public string Title => Item.Title;

    /// <summary>
    ///     The title with a leading issue code removed, since the badge beside it
    ///     already shows the code. The full heading stays available as the tooltip.
    /// </summary>
    public string DisplayTitle
    {
        get
        {
            if (IsEpic || Item.Code is not { Length: > 0 } code) return Item.Title;

            return Item.Title.StartsWith(code, StringComparison.Ordinal)
                ? Item.Title[code.Length..].TrimStart(' ', '\t', '·', '-', '—', ':')
                : Item.Title;
        }
    }

    /// <summary>
    ///     The editor buffer for this item's description block. Two-way bound; every
    ///     change recomputes the derived views below from the buffer's text.
    /// </summary>
    public string Source
    {
        get => _source;
        set => SetEditedSource(value);
    }

    /// <summary>The text as parsed, to compare against and to discard back to.</summary>
    public string OriginalSource => _originalSource;

    /// <summary>True while the buffer differs from what the file holds.</summary>
    public bool IsDirty { get; private set; }

    /// <summary>The description as authored, split the way the parser splits it.</summary>
    public IReadOnlyList<string> EditedLines =>
        [.. PythonCompat.SplitLines(Source)];

    /// <summary>
    ///     Applies an edited buffer. Returns true when the text actually changed —
    ///     the two-way binding calls this on every keystroke, including ones that
    ///     only move the caret.
    /// </summary>
    public bool SetEditedSource(string text)
    {
        if (text == _source)
        {
            return false;
        }

        _source = text;
        IsDirty = text != _originalSource;
        Recompute();
        OnPropertyChanged(nameof(Source));
        OnPropertyChanged(nameof(IsDirty));
        return true;
    }

    /// <summary>Throws the buffer away and returns to the text as parsed.</summary>
    public void DiscardEdits()
    {
        _source = _originalSource;
        IsDirty = false;
        Recompute();
        OnPropertyChanged(nameof(Source));
        OnPropertyChanged(nameof(IsDirty));
    }

    public string Html { get; private set; } = string.Empty;

    /// <summary>The same markup, indented for reading. Display only.</summary>
    public string FormattedHtml { get; private set; } = string.Empty;

    /// <summary>The description as it will read on the board.</summary>
    public PreviewDocument Preview { get; private set; } = PreviewDocument.Parse(string.Empty);

    public IReadOnlyList<TaskLine> Tasks { get; private set; } = [];

    public IReadOnlyList<MarkupProblem> Problems { get; private set; } = [];

    public bool HasProblems => Problems.Count > 0;

    public string Detail => IsEpic
        ? $"{Children.Count} issue{(Children.Count == 1 ? string.Empty : "s")}"
        : $"{Tasks.Count} task{(Tasks.Count == 1 ? string.Empty : "s")}";

    public string ProblemSummary => Problems.Count switch
    {
        0 => "Markup is well formed.",
        1 => "1 markup problem — Apply would be blocked.",
        var n => $"{n} markup problems — Apply would be blocked."
    };

    /// <summary>
    ///     Recomputes every derived view from <see cref="Source" />. The item passed
    ///     to the audit is the parsed original with the buffer's lines — identity and
    ///     level from the file, content from the editor — so an edited bullet is
    ///     audited exactly as the same text would be after a save and re-parse.
    /// </summary>
    private void Recompute()
    {
        var lines = EditedLines;
        var audited = Item with
        {
            DescriptionLines = lines,
            Bullets = BacklogParser.BulletsOf(Item.Level, lines)
        };

        // Parsed from Html, not from the source lines, so the preview cannot
        // disagree with what would be written to the board.
        Html = MarkdownHtml.ToHtml(lines);
        Preview = PreviewDocument.Parse(Html);
        FormattedHtml = HtmlLayout.Format(Html);

        Tasks = [.. audited.Bullets.Select(b => new TaskLine(MarkdownHtml.Plain(b), MarkdownHtml.Inline(b)))];

        Problems =
        [
            .. BacklogMarkupAudit.ProblemsFor(audited).Select(p => new MarkupProblem(p.Scope, p.Message))
        ];

        OnPropertyChanged(nameof(Html));
        OnPropertyChanged(nameof(FormattedHtml));
        OnPropertyChanged(nameof(Preview));
        OnPropertyChanged(nameof(Tasks));
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(Problems));
        OnPropertyChanged(nameof(HasProblems));
        OnPropertyChanged(nameof(ProblemSummary));
    }
}
