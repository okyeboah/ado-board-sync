using System.Text;
using System.Text.RegularExpressions;

namespace AdoBoardSync.Desktop.Preview;

public enum PreviewBlockKind
{
    Paragraph,
    Bullet,
    Rule,
    Table,
}

/// <summary>A stretch of text with the formatting that applies to it.</summary>
public sealed record PreviewRun(string Text, bool Bold, bool Italic, bool Code);

public sealed record PreviewRow(bool IsHeader, IReadOnlyList<IReadOnlyList<PreviewRun>> Cells);

public sealed record PreviewBlock
{
    public required PreviewBlockKind Kind { get; init; }

    /// <summary>Nesting level of a bullet, zero for the outermost list.</summary>
    public int Depth { get; init; }

    public IReadOnlyList<PreviewRun> Runs { get; init; } = [];

    public IReadOnlyList<PreviewRow> Rows { get; init; } = [];
}

/// <summary>
/// The description as it will read on the board, parsed from the markup the app
/// would send rather than re-derived from the Markdown source.
///
/// Parsing the generated HTML is what makes the preview honest: a second Markdown
/// renderer could disagree with <see cref="Core.Markdown.MarkdownHtml"/> and show
/// the user something Azure DevOps will never receive. The grammar is closed —
/// the converter emits only p, ul, li, hr, table, tr, th, td, b, i and code — so
/// anything outside it is a converter change this parser must be taught about.
/// </summary>
public sealed partial record PreviewDocument(IReadOnlyList<PreviewBlock> Blocks)
{
    public bool IsEmpty => Blocks.Count == 0;

    [GeneratedRegex(@"<(/?)([a-zA-Z]+)(?:\s[^>]*)?>")]
    private static partial Regex Tag { get; }

    public static PreviewDocument Parse(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return new PreviewDocument([]);
        }

        var state = new ParseState();

        var position = 0;
        foreach (Match tag in Tag.Matches(html))
        {
            state.AddText(html[position..tag.Index]);
            position = tag.Index + tag.Length;
            state.ApplyTag(tag.Groups[2].Value.ToLowerInvariant(), isClosing: tag.Groups[1].Value == "/");
        }

        state.AddText(html[position..]);
        state.Flush();

        return new PreviewDocument(state.Blocks);
    }

    private sealed class ParseState
    {
        private readonly List<PreviewRun> _runs = [];
        private readonly List<PreviewRow> _rows = [];
        private readonly List<IReadOnlyList<PreviewRun>> _cells = [];

        private PreviewBlockKind? _open;
        private int _depth;
        private bool _bold;
        private bool _italic;
        private bool _code;
        private bool _headerRow;

        public List<PreviewBlock> Blocks { get; } = [];

        public void AddText(string raw)
        {
            if (raw.Length == 0)
            {
                return;
            }

            // Newlines are the converter's own line joins between tags, not content.
            var text = Unescape(raw).Replace("\n", " ", StringComparison.Ordinal);
            if (text.Length == 0 || (_open is null && string.IsNullOrWhiteSpace(text)))
            {
                return;
            }

            // Inline conversion (a Task title) emits formatted text with no block
            // tag around it, so text outside any block opens one.
            _open ??= PreviewBlockKind.Paragraph;

            _runs.Add(new PreviewRun(text, _bold, _italic, _code));
        }

        public void ApplyTag(string name, bool isClosing)
        {
            switch (name)
            {
                case "b" or "strong":
                    _bold = !isClosing;
                    return;
                case "i" or "em":
                    _italic = !isClosing;
                    return;
                case "code":
                    _code = !isClosing;
                    return;

                case "p":
                    Flush();
                    _open = isClosing ? null : PreviewBlockKind.Paragraph;
                    return;

                case "ul":
                    // A nested list opens inside its parent's <li>, so the parent
                    // item is complete by the time this arrives.
                    Flush();
                    _depth += isClosing ? -1 : 1;
                    _depth = Math.Max(0, _depth);
                    return;

                case "li":
                    Flush();
                    _open = isClosing ? null : PreviewBlockKind.Bullet;
                    return;

                case "hr":
                    Flush();
                    Blocks.Add(new PreviewBlock { Kind = PreviewBlockKind.Rule });
                    return;

                case "table":
                    Flush();
                    if (isClosing)
                    {
                        FlushTable();
                    }
                    return;

                case "tr":
                    if (isClosing)
                    {
                        _rows.Add(new PreviewRow(_headerRow, [.. _cells]));
                        _cells.Clear();
                        _headerRow = false;
                    }
                    return;

                case "th" or "td":
                    if (isClosing)
                    {
                        _cells.Add(Trim(_runs));
                        _runs.Clear();
                        _open = null;
                    }
                    else
                    {
                        _runs.Clear();
                        _open = PreviewBlockKind.Table;
                        _headerRow |= name == "th";
                    }
                    return;
            }
        }

        public void Flush()
        {
            if (_open is not (PreviewBlockKind.Paragraph or PreviewBlockKind.Bullet) || _runs.Count == 0)
            {
                _runs.Clear();
                return;
            }

            var runs = Trim(_runs);
            _runs.Clear();

            if (runs.Count > 0)
            {
                Blocks.Add(new PreviewBlock
                {
                    Kind = _open.Value,
                    Depth = _open == PreviewBlockKind.Bullet ? Math.Max(0, _depth - 1) : 0,
                    Runs = runs,
                });
            }

            _open = null;
        }

        private void FlushTable()
        {
            if (_rows.Count > 0)
            {
                Blocks.Add(new PreviewBlock { Kind = PreviewBlockKind.Table, Rows = [.. _rows] });
                _rows.Clear();
            }
        }

        /// <summary>
        /// Trims the block's outer edges. The converter joins its output with
        /// newlines, so a parent bullet arrives as "Parent " with the line break
        /// before its nested list folded into a trailing space. Inner whitespace
        /// is the text's own and stays.
        /// </summary>
        private static IReadOnlyList<PreviewRun> Trim(List<PreviewRun> runs)
        {
            var trimmed = new List<PreviewRun>(runs);

            while (trimmed.Count > 0)
            {
                var first = trimmed[0] with { Text = trimmed[0].Text.TrimStart() };
                if (first.Text.Length > 0)
                {
                    trimmed[0] = first;
                    break;
                }

                trimmed.RemoveAt(0);
            }

            while (trimmed.Count > 0)
            {
                var last = trimmed[^1] with { Text = trimmed[^1].Text.TrimEnd() };
                if (last.Text.Length > 0)
                {
                    trimmed[^1] = last;
                    break;
                }

                trimmed.RemoveAt(trimmed.Count - 1);
            }

            return trimmed;
        }

        /// <summary>
        /// Reverses <c>EscapeHtml</c>. Ampersand goes last, so an escaped
        /// <c>&amp;amp;lt;</c> does not decode twice into a tag.
        /// </summary>
        private static string Unescape(string text) =>
            new StringBuilder(text)
                .Replace("&lt;", "<")
                .Replace("&gt;", ">")
                .Replace("&quot;", "\"")
                .Replace("&#39;", "'")
                .Replace("&nbsp;", " ")
                .Replace("&amp;", "&")
                .ToString();
    }
}
