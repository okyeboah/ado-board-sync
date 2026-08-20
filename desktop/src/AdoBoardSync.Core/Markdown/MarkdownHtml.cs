using System.Text;
using System.Text.RegularExpressions;

namespace AdoBoardSync.Core.Markdown;

/// <summary>
/// Markdown to HTML conversion for work-item descriptions and Task titles.
///
/// This is a port of the CLI's <c>htmlfmt.py</c> and must stay byte-for-byte
/// identical to it: the desktop preview pane shows what this produces, and the
/// same output is written to <c>System.Description</c>. A divergence here would
/// make the preview a lie. <c>AdoBoardSync.Parity.Tests</c> runs both
/// implementations over shared fixtures and fails the build on any difference.
/// </summary>
public static partial class MarkdownHtml
{
    // Azure DevOps renders System.Description as raw HTML with no stylesheet of its
    // own, so a bare <table> arrives without borders. The styles ride inline.
    private const string TableStyle = "border-collapse:collapse;margin:8px 0;";
    private const string CellStyle = "border:1px solid #c8c8c8;padding:4px 8px;text-align:left;vertical-align:top;";
    private const string HeadStyle = CellStyle + "background:#f4f4f4;font-weight:600;";

    [GeneratedRegex(@"^(\s*)[-*+]\s+(.*)$")]
    private static partial Regex Bullet { get; }

    [GeneratedRegex(@"^\s*\|.*\|\s*$")]
    private static partial Regex TableRow { get; }

    [GeneratedRegex(@"^\s*\|[\s:|-]+\|\s*$")]
    private static partial Regex TableSeparator { get; }

    [GeneratedRegex(@"^\s*(-{3,}|\*{3,}|_{3,})\s*$")]
    private static partial Regex HorizontalRule { get; }

    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex CodeSpan { get; }

    [GeneratedRegex(@"\x00(\d+)\x00")]
    private static partial Regex CodeSentinel { get; }

    [GeneratedRegex(@"\*\*([^*]+)\*\*")]
    private static partial Regex Bold { get; }

    [GeneratedRegex(@"\*([^*]+)\*")]
    private static partial Regex Italic { get; }

    [GeneratedRegex(@"</?[^>]+>")]
    private static partial Regex AnyTag { get; }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace { get; }

    public static string EscapeHtml(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>
    /// Lifts <c>`code`</c> spans out so the bold/italic passes cannot reach inside them.
    ///
    /// A backlog says things like <c>SharedKernel.*</c> and <c>*Command</c>. Left in
    /// place, the italics pass pairs those asterisks across the spans and emits
    /// interleaved i/code tags that no browser can nest.
    /// </summary>
    private static (string Text, List<string> Spans) ProtectCode(string text)
    {
        var spans = new List<string>();
        var protectedText = CodeSpan.Replace(text, match =>
        {
            spans.Add(match.Groups[1].Value);
            return $"\u0000{spans.Count - 1}\u0000";
        });

        return (protectedText, spans);
    }

    private static string RestoreCode(string text, List<string> spans) =>
        CodeSentinel.Replace(text, m => $"<code>{spans[int.Parse(m.Groups[1].Value)]}</code>");

    public static string ReplaceInlineMarkdown(string text)
    {
        var (protectedText, spans) = ProtectCode(text);
        protectedText = Bold.Replace(protectedText, "<b>$1</b>");
        protectedText = Italic.Replace(protectedText, "<i>$1</i>");
        return RestoreCode(protectedText, spans);
    }

    private static string Format(string text) => ReplaceInlineMarkdown(EscapeHtml(text));

    private static List<string> Cells(string row) =>
        [.. row.Trim().Trim('|').Split('|').Select(c => c.Trim())];

    private enum BlockKind
    {
        Blank,
        Rule,
        Row,
        BulletItem,
        Text
    }

    private sealed class Block
    {
        public required BlockKind Kind { get; init; }
        public string Text { get; set; } = string.Empty;
        public int Indent { get; init; }
    }

    /// <summary>
    /// Groups raw lines into blocks, joining wrapped continuation lines.
    ///
    /// A backlog wraps prose across source lines. Each such line is part of the
    /// bullet or paragraph above it, so it is joined rather than made a block of
    /// its own. A blank line ends the block, which is what separates them.
    /// </summary>
    private static List<Block> Fold(IEnumerable<string> lines)
    {
        var blocks = new List<Block>();

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            var stripped = line.Trim();
            var last = blocks.Count > 0 ? blocks[^1] : null;

            if (stripped.Length == 0)
            {
                blocks.Add(new Block { Kind = BlockKind.Blank });
            }
            else if (PythonCompat.MatchAtStart(HorizontalRule, line))
            {
                blocks.Add(new Block { Kind = BlockKind.Rule });
            }
            else if (PythonCompat.MatchAtStart(TableRow, line))
            {
                blocks.Add(new Block { Kind = BlockKind.Row, Text = stripped });
            }
            else if (PythonCompat.MatchAtStart(Bullet, line, out var bulletMatch))
            {
                blocks.Add(new Block
                {
                    Kind = BlockKind.BulletItem,
                    Indent = bulletMatch.Groups[1].Value.Length,
                    Text = bulletMatch.Groups[2].Value.Trim()
                });
            }
            else if (last is { Kind: BlockKind.BulletItem or BlockKind.Text })
            {
                last.Text += " " + stripped;
            }
            else
            {
                blocks.Add(new Block { Kind = BlockKind.Text, Text = stripped });
            }
        }

        return blocks;
    }

    /// <summary>Renders collected table rows. The |---| separator row carries no data.</summary>
    private static List<string> TableLines(List<string> rows)
    {
        var dataRows = rows.Where(r => !PythonCompat.MatchAtStart(TableSeparator, r)).ToList();
        if (dataRows.Count == 0)
        {
            return [];
        }

        var head = string.Concat(Cells(dataRows[0]).Select(c => $"<th style=\"{HeadStyle}\">{Format(c)}</th>"));
        var output = new List<string> { $"<table style=\"{TableStyle}\">", $"<tr>{head}</tr>" };

        foreach (var row in dataRows.Skip(1))
        {
            var body = string.Concat(Cells(row).Select(c => $"<td style=\"{CellStyle}\">{Format(c)}</td>"));
            output.Add($"<tr>{body}</tr>");
        }

        output.Add("</table>");
        return output;
    }

    private static void CloseListItem(List<string> output) => output[^1] += "</li>";

    /// <summary>Adjusts the open ul stack so a bullet at <paramref name="indent"/> can be appended.</summary>
    private static void OpenBullet(List<string> output, List<int> stack, int indent)
    {
        if (stack.Count == 0 || indent > stack[^1])
        {
            output.Add("<ul>");              // a deeper level nests in the open <li>
            stack.Add(indent);
            return;
        }

        while (stack.Count > 1 && indent < stack[^1])
        {
            CloseListItem(output);
            output.Add("</ul>");
            stack.RemoveAt(stack.Count - 1);
        }

        CloseListItem(output);               // close the previous sibling
    }

    /// <summary>Converts markdown lines (bullets, tables, bold, italics, code) to HTML.</summary>
    public static string ToHtml(IEnumerable<string> lines)
    {
        var output = new List<string>();
        var stack = new List<int>();         // indent of each open <ul>
        var table = new List<string>();

        void CloseLists()
        {
            while (stack.Count > 0)
            {
                CloseListItem(output);
                output.Add("</ul>");
                stack.RemoveAt(stack.Count - 1);
            }
        }

        void FlushTable()
        {
            if (table.Count > 0)
            {
                output.AddRange(TableLines(table));
                table.Clear();
            }
        }

        foreach (var block in Fold(lines))
        {
            if (block.Kind == BlockKind.Row)
            {
                CloseLists();
                table.Add(block.Text);
                continue;
            }

            FlushTable();

            switch (block.Kind)
            {
                case BlockKind.Rule:
                    CloseLists();
                    output.Add("<hr>");
                    break;
                case BlockKind.BulletItem:
                    OpenBullet(output, stack, block.Indent);
                    output.Add($"<li>{Format(block.Text)}");
                    break;
                case BlockKind.Text:
                    CloseLists();
                    output.Add($"<p>{Format(block.Text)}</p>");
                    break;
                default:
                    // Blank: a blank line ends a block but emits nothing itself.
                    // Row is handled above and never reaches here.
                    break;
            }
        }

        FlushTable();
        CloseLists();
        return string.Join("\n", output);
    }

    /// <summary>Minimal inline markdown to HTML used for Task descriptions.</summary>
    public static string Inline(string text)
    {
        var escaped = EscapeHtml(text);
        var (protectedText, spans) = ProtectCode(escaped);
        protectedText = Bold.Replace(protectedText, "<b>$1</b>");
        return RestoreCode(protectedText, spans);
    }

    /// <summary>Strips inline markdown to plain text (used to build and compare Task titles).</summary>
    public static string Plain(string text)
    {
        var stripped = CodeSpan.Replace(text, "$1");
        stripped = Bold.Replace(stripped, "$1");
        return Whitespace.Replace(stripped, " ").Trim();
    }

    /// <summary>Normalises HTML to plain lowercase text so HTML artifacts are not flagged as drift.</summary>
    public static string Normalize(string? html)
    {
        var text = AnyTag.Replace(html ?? string.Empty, " ");
        var builder = new StringBuilder(text)
            .Replace("&amp;", "&")
            .Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Replace("&nbsp;", " ")
            .Replace("&quot;", "\"")
            .Replace("&apos;", "'")
            .Replace("&#39;", "'");

        return Whitespace.Replace(builder.ToString(), " ").Trim().ToLowerInvariant();
    }

}
