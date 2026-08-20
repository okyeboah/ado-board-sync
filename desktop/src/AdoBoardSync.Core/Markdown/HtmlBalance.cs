namespace AdoBoardSync.Core.Markdown;

/// <summary>
/// Finds unbalanced tags in description markup.
///
/// A port of the CLI's <c>htmlfmt.unbalanced</c>, which backs the <c>check-html</c>
/// command. Azure DevOps stores the description as HTML and shows it unchanged, so
/// interleaved or unclosed tags reach the browser. Nothing else catches that, which
/// is why the editor blocks Apply on any problem this reports.
/// </summary>
public static class HtmlBalance
{
    private static readonly HashSet<string> VoidTags = new(StringComparer.Ordinal) { "hr", "br", "img" };

    /// <summary>
    /// Returns the tag problems in <paramref name="markup"/>; an empty list means it
    /// is well formed.
    /// </summary>
    public static IReadOnlyList<string> Problems(string markup)
    {
        var stack = new List<string>();
        var problems = new List<string>();

        void HandleStart(string tag)
        {
            if (!VoidTags.Contains(tag))
            {
                stack.Add(tag);
            }
        }

        void HandleEnd(string tag)
        {
            if (stack.Count == 0)
            {
                problems.Add($"</{tag}> closes nothing");
            }
            else if (stack[^1] != tag)
            {
                problems.Add($"</{tag}> closes an open <{stack[^1]}>");
            }
            else
            {
                stack.RemoveAt(stack.Count - 1);
            }
        }

        var index = 0;
        while (index < markup.Length)
        {
            var open = markup.IndexOf('<', index);
            if (open < 0)
            {
                break;
            }

            index = ScanTag(markup, open, HandleStart, HandleEnd);
        }

        problems.AddRange(stack.Select(tag => $"<{tag}> never closes"));
        return problems;
    }

    /// <summary>
    /// Consumes one markup construct starting at <paramref name="open"/> and returns the
    /// index just past it, mirroring how Python's HTMLParser classifies the same text.
    /// </summary>
    private static int ScanTag(string markup, int open, Action<string> onStart, Action<string> onEnd)
    {
        if (markup.AsSpan(open).StartsWith("<!--"))
        {
            var close = markup.IndexOf("-->", open + 4, StringComparison.Ordinal);
            return close < 0 ? markup.Length : close + 3;
        }

        if (open + 1 < markup.Length && (markup[open + 1] == '!' || markup[open + 1] == '?'))
        {
            var close = markup.IndexOf('>', open + 1);
            return close < 0 ? markup.Length : close + 1;
        }

        var isEnd = open + 1 < markup.Length && markup[open + 1] == '/';
        var nameStart = isEnd ? open + 2 : open + 1;

        // HTMLParser treats a '<' that is not followed by a tag name as plain text.
        if (nameStart >= markup.Length || !char.IsAsciiLetter(markup[nameStart]))
        {
            return open + 1;
        }

        var nameEnd = nameStart;
        while (nameEnd < markup.Length && !IsNameTerminator(markup[nameEnd]))
        {
            nameEnd++;
        }

        var tag = markup[nameStart..nameEnd].ToLowerInvariant();
        var tagClose = markup.IndexOf('>', nameEnd);
        if (tagClose < 0)
        {
            // An unterminated tag is never reported by HTMLParser, which waits for more input.
            return markup.Length;
        }

        if (isEnd)
        {
            onEnd(tag);
        }
        else if (markup[tagClose - 1] == '/')
        {
            // HTMLParser's default handle_startendtag calls handle_starttag then handle_endtag.
            onStart(tag);
            onEnd(tag);
        }
        else
        {
            onStart(tag);
        }

        return tagClose + 1;
    }

    private static bool IsNameTerminator(char c) =>
        c is ' ' or '\t' or '\n' or '\r' or '\f' or '/' or '>';
}
