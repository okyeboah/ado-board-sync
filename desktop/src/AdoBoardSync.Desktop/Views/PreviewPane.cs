using AdoBoardSync.Desktop.Preview;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace AdoBoardSync.Desktop.Views;

/// <summary>
/// Renders a <see cref="PreviewDocument"/> as the description will read on the
/// board. Built in code rather than in XAML because a paragraph's inline runs
/// cannot be templated: the number and formatting of runs is data, not layout.
/// </summary>
public sealed class PreviewPane : Decorator
{
    public static readonly StyledProperty<PreviewDocument?> DocumentProperty =
        AvaloniaProperty.Register<PreviewPane, PreviewDocument?>(nameof(Document));

    static PreviewPane() =>
        DocumentProperty.Changed.AddClassHandler<PreviewPane>((pane, _) => pane.Rebuild());

    public PreviewDocument? Document
    {
        get => GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    private void Rebuild()
    {
        if (Document is not { IsEmpty: false } document)
        {
            Child = new TextBlock
            {
                Text = "This item has no description.",
                Classes = { "caption" },
            };
            return;
        }

        var stack = new StackPanel { Spacing = 8 };
        foreach (var block in document.Blocks)
        {
            stack.Children.Add(Render(block));
        }

        Child = stack;
    }

    private static Control Render(PreviewBlock block) => block.Kind switch
    {
        PreviewBlockKind.Bullet => Bullet(block),
        PreviewBlockKind.Rule => Rule(),
        PreviewBlockKind.Table => Table(block),
        _ => Paragraph(block.Runs),
    };

    private static TextBlock Paragraph(IReadOnlyList<PreviewRun> runs)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, Classes = { "body" } };
        foreach (var run in runs)
        {
            text.Inlines!.Add(Inline(run));
        }

        return text;
    }

    private static Control Bullet(PreviewBlock block)
    {
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 8,
            Margin = new Thickness(block.Depth * 18, 0, 0, 0),
        };

        // A glyph, not a bullet character alone: nested levels must stay
        // distinguishable when the indent is the only other cue.
        var marker = new TextBlock
        {
            Text = block.Depth == 0 ? "•" : "◦",
            Classes = { "body" },
            VerticalAlignment = VerticalAlignment.Top,
        };

        var body = Paragraph(block.Runs);
        Grid.SetColumn(body, 1);

        row.Children.Add(marker);
        row.Children.Add(body);
        return row;
    }

    private static Control Rule()
    {
        var line = new Border { Height = 1, Margin = new Thickness(0, 4, 0, 4) };
        line.Bind(Border.BackgroundProperty, new DynamicResourceExtension("CardBorderBrush"));
        return line;
    }

    private static Control Table(PreviewBlock block)
    {
        var columns = block.Rows.Max(r => r.Cells.Count);
        var grid = new Grid();

        for (var column = 0; column < columns; column++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        }

        for (var rowIndex = 0; rowIndex < block.Rows.Count; rowIndex++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var row = block.Rows[rowIndex];
            for (var column = 0; column < columns; column++)
            {
                var content = Paragraph(column < row.Cells.Count ? row.Cells[column] : []);
                if (row.IsHeader)
                {
                    content.FontWeight = FontWeight.SemiBold;
                }

                var cell = new Border
                {
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(8, 4),
                    Child = content,
                };

                cell.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("CardBorderBrush"));

                Grid.SetRow(cell, rowIndex);
                Grid.SetColumn(cell, column);
                grid.Children.Add(cell);
            }
        }

        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = grid,
        };
    }

    private static Run Inline(PreviewRun run)
    {
        var inline = new Run(run.Text);

        if (run.Bold)
        {
            inline.FontWeight = FontWeight.SemiBold;
        }

        if (run.Italic)
        {
            inline.FontStyle = FontStyle.Italic;
        }

        if (run.Code)
        {
            inline.Bind(TextElement.FontFamilyProperty, new DynamicResourceExtension("EditorFontFamily"));
            inline.Bind(TextElement.ForegroundProperty, new DynamicResourceExtension("AppAccentBrush"));
        }

        return inline;
    }
}
