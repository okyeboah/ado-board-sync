using System.Text;
using System.Text.RegularExpressions;

namespace AdoBoardSync.Desktop.Preview;

/// <summary>
/// Indents generated markup so it can be read.
///
/// Display only. The converter emits one tag per line with no indentation, which
/// is what goes on the wire; this adds whitespace between tags purely so a person
/// can follow the nesting. Nothing here may reach
/// <see cref="Core.Board.IBoardGateway"/> — the pane that shows this says so.
/// </summary>
public static partial class HtmlLayout
{
    /// <summary>Tags that never break a line: they format words inside one.</summary>
    private static readonly HashSet<string> InlineTags =
        new(StringComparer.OrdinalIgnoreCase) { "b", "i", "em", "strong", "code" };

    /// <summary>Tags whose children are indented one level further.</summary>
    private static readonly HashSet<string> ContainerTags =
        new(StringComparer.OrdinalIgnoreCase) { "ul", "ol", "table" , "tr" };

    [GeneratedRegex(@"<(/?)([a-zA-Z]+)(?:\s[^>]*)?>")]
    private static partial Regex Tag { get; }

    public static string Format(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var writer = new LineWriter();

        var position = 0;
        foreach (Match tag in Tag.Matches(html))
        {
            writer.AddText(html[position..tag.Index]);
            position = tag.Index + tag.Length;

            var name = tag.Groups[2].Value;
            var isClosing = tag.Groups[1].Value == "/";

            if (InlineTags.Contains(name))
            {
                writer.Append(tag.Value);
                continue;
            }

            if (ContainerTags.Contains(name))
            {
                if (isClosing)
                {
                    writer.Outdent();
                    writer.StartLine();
                    writer.Append(tag.Value);
                }
                else
                {
                    writer.StartLine();
                    writer.Append(tag.Value);
                    writer.Indent();
                }

                continue;
            }

            // A block whose content stays on its line: p, li, td, th, hr. Its
            // closing tag joins whatever line is open, so "</ul></li>" stays put.
            if (!isClosing)
            {
                writer.StartLine();
            }

            writer.Append(tag.Value);
        }

        writer.AddText(html[position..]);
        return writer.ToString();
    }

    private sealed class LineWriter
    {
        private readonly List<string> _lines = [];
        private readonly StringBuilder _current = new();
        private int _depth;
        private int _lineDepth;

        public void Indent() => _depth++;

        public void Outdent() => _depth = Math.Max(0, _depth - 1);

        public void StartLine()
        {
            Flush();
            _lineDepth = _depth;
        }

        public void Append(string text) => _current.Append(text);

        public void AddText(string raw)
        {
            if (raw.Length == 0)
            {
                return;
            }

            // The converter joins its output lines with newlines, and a block's
            // content is always on one line — so no newline here is ever content.
            // A whitespace run carrying one is a join and goes entirely; otherwise
            // only the newline goes, since "Parent\n" would leave a blank line.
            // A space between two inline tags has no newline and is part of the
            // sentence, so it survives.
            if (raw.Contains('\n', StringComparison.Ordinal) && string.IsNullOrWhiteSpace(raw))
            {
                return;
            }

            _current.Append(raw.Replace("\n", string.Empty, StringComparison.Ordinal));
        }

        private void Flush()
        {
            if (_current.Length == 0)
            {
                return;
            }

            _lines.Add(new string(' ', _lineDepth * 2) + _current);
            _current.Clear();
        }

        public override string ToString()
        {
            Flush();
            return string.Join('\n', _lines);
        }
    }
}
