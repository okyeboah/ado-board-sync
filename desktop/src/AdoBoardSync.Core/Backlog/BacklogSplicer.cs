namespace AdoBoardSync.Core.Backlog;

/// <summary>
/// Rewrites one parsed item's description block inside the backlog text.
///
/// The editor edits a buffer; saving splices every edited block back into the
/// file through here. Where each block lives comes from the parser's own
/// <see cref="BacklogItem.DescriptionStart"/>/<see cref="BacklogItem.DescriptionEnd"/>
/// ranges, so the splice cannot disagree with the parse about what belongs to the
/// item. Everything outside the replaced ranges — other items, headings, content
/// after a stop heading — passes through untouched.
/// </summary>
public static class BacklogSplicer
{
    /// <summary>
    /// Returns the backlog with <paramref name="item"/>'s description block
    /// replaced by <paramref name="newDescription"/>. The file's line-ending
    /// style and its trailing newline are preserved; ranges refer to the
    /// original text, so callers applying several edits must go last-to-first.
    /// </summary>
    public static string ReplaceDescription(string markdown, BacklogItem item, string newDescription)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(item.DescriptionStart);
        ArgumentOutOfRangeException.ThrowIfLessThan(item.DescriptionEnd, item.DescriptionStart);

        var lines = PythonCompat.SplitLines(markdown).ToList();
        ArgumentOutOfRangeException.ThrowIfGreaterThan(item.DescriptionEnd, lines.Count);

        // StringReader semantics drop a single trailing terminator, so a buffer
        // that ends with one newline splices exactly like one that does not —
        // matching the parser, which reads the same way.
        var replacement = PythonCompat.SplitLines(newDescription).ToList();

        // Trailing blank lines inside a block are the separator between this item
        // and the next heading, not description content: the parser keeps them in
        // DescriptionLines, but they render nothing. They survive the splice —
        // and a buffer's own trailing blanks do not double them — so an edit
        // cannot silently change the file's spacing, and the ranges of later
        // items stay valid across several splices.
        var contentEnd = item.DescriptionEnd;
        while (contentEnd > item.DescriptionStart && lines[contentEnd - 1].Trim().Length == 0)
        {
            contentEnd--;
        }

        var preservedBlanks = lines.Skip(contentEnd).Take(item.DescriptionEnd - contentEnd);
        while (replacement.Count > 0 && replacement[^1].Trim().Length == 0)
        {
            replacement.RemoveAt(replacement.Count - 1);
        }

        var eol = markdown.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var trailingNewline = markdown.EndsWith('\n') || markdown.EndsWith('\r');

        var result = new List<string>(lines.Count - (item.DescriptionEnd - item.DescriptionStart) + replacement.Count);
        result.AddRange(lines.Take(item.DescriptionStart));
        result.AddRange(replacement);
        result.AddRange(preservedBlanks);
        result.AddRange(lines.Skip(item.DescriptionEnd));

        return string.Join(eol, result) + (trailingNewline ? eol : string.Empty);
    }
}
