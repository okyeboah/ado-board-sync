namespace AdoBoardSync.Core.Diff;

public enum DiffLineKind
{
    Unchanged,
    Removed,
    Added,
}

/// <summary>
/// One line of a rendered diff. <see cref="OriginalLine" /> is null on an added line
/// and <see cref="RevisedLine" /> is null on a removed one, both 1-based, so a
/// reviewer can point at a line in either file rather than at an offset into the
/// diff.
/// </summary>
public sealed record DiffLine(DiffLineKind Kind, string Text, int? OriginalLine, int? RevisedLine)
{
    public string Marker => Kind switch
    {
        DiffLineKind.Added => "+",
        DiffLineKind.Removed => "-",
        _ => " ",
    };
}

public sealed record TextDiffResult(IReadOnlyList<DiffLine> Lines, int AddedCount, int RemovedCount)
{
    public bool HasChanges => AddedCount > 0 || RemovedCount > 0;

    /// <summary>
    /// True when the whole changed region was reported as one replacement rather
    /// than matched line by line — see <see cref="TextDiff.MaxCells" />. Surfaces
    /// say so, because "every line changed" is otherwise indistinguishable from an
    /// agent that really did rewrite the file.
    /// </summary>
    public bool IsCoarse { get; init; }

    public string Summary => HasChanges
        ? $"+{AddedCount} −{RemovedCount}"
        : "no change";
}

/// <summary>
/// A line-level diff of two texts, by longest common subsequence.
///
/// Written here rather than shelled out to <c>diff</c>: the desktop app must show
/// the same review on a machine that has no POSIX tools, and a diff produced by
/// spawning a process on a file an agent just wrote is one race away from
/// describing something other than what the reviewer is about to accept.
///
/// Lines are split with <see cref="PythonCompat.SplitLines" />, the same rule
/// <see cref="Backlog.BacklogParser" /> applies, so a line in the diff is a line in
/// the parse. <c>string.Split</c> would disagree with the parser about FF and NEL.
/// </summary>
public static class TextDiff
{
    /// <summary>
    /// The largest LCS table this will allocate — 4M cells, 16MB of <c>int</c>.
    /// Beyond it the changed region is reported as a whole-block replacement
    /// instead. The quadratic table is what makes an agent that rewrites a
    /// 20,000-line backlog cost gigabytes; a coarse diff of a rewrite that large is
    /// no worse to read, and it is bounded.
    /// </summary>
    private const long MaxCells = 4_000_000;

    public static TextDiffResult Between(string original, string revised)
    {
        var left = PythonCompat.SplitLines(original).ToList();
        var right = PythonCompat.SplitLines(revised).ToList();

        // Trimming the shared head and tail first is not only an optimisation: a
        // one-line edit to a long backlog leaves a middle small enough to match
        // exactly, which is the case a reviewer actually meets.
        var start = 0;
        while (start < left.Count && start < right.Count && left[start] == right[start])
        {
            start++;
        }

        var leftEnd = left.Count;
        var rightEnd = right.Count;
        while (leftEnd > start && rightEnd > start && left[leftEnd - 1] == right[rightEnd - 1])
        {
            leftEnd--;
            rightEnd--;
        }

        var lines = new List<DiffLine>(left.Count + right.Count);
        for (var i = 0; i < start; i++)
        {
            lines.Add(new DiffLine(DiffLineKind.Unchanged, left[i], i + 1, i + 1));
        }

        var n = leftEnd - start;
        var m = rightEnd - start;
        var coarse = (long)(n + 1) * (m + 1) > MaxCells;
        if (coarse)
        {
            for (var i = 0; i < n; i++)
            {
                lines.Add(new DiffLine(DiffLineKind.Removed, left[start + i], start + i + 1, null));
            }

            for (var j = 0; j < m; j++)
            {
                lines.Add(new DiffLine(DiffLineKind.Added, right[start + j], null, start + j + 1));
            }
        }
        else
        {
            Match(left, right, start, n, m, lines);
        }

        for (var i = leftEnd; i < left.Count; i++)
        {
            lines.Add(new DiffLine(DiffLineKind.Unchanged, left[i], i + 1, i - leftEnd + rightEnd + 1));
        }

        var added = 0;
        var removed = 0;
        foreach (var line in lines)
        {
            if (line.Kind == DiffLineKind.Added)
            {
                added++;
            }
            else if (line.Kind == DiffLineKind.Removed)
            {
                removed++;
            }
        }

        return new TextDiffResult(lines, added, removed) { IsCoarse = coarse && (added > 0 || removed > 0) };
    }

    /// <summary>
    /// Walks the changed middle, emitting in document order. The table is filled
    /// backwards so the walk can run forwards: emitting as it goes keeps the diff in
    /// reading order without a reversal pass.
    /// </summary>
    private static void Match(
        IReadOnlyList<string> left,
        IReadOnlyList<string> right,
        int start,
        int n,
        int m,
        List<DiffLine> lines)
    {
        var table = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                table[i, j] = left[start + i] == right[start + j]
                    ? table[i + 1, j + 1] + 1
                    : Math.Max(table[i + 1, j], table[i, j + 1]);
            }
        }

        var x = 0;
        var y = 0;
        while (x < n && y < m)
        {
            if (left[start + x] == right[start + y])
            {
                lines.Add(new DiffLine(
                    DiffLineKind.Unchanged, left[start + x], start + x + 1, start + y + 1));
                x++;
                y++;
            }
            else if (table[x + 1, y] >= table[x, y + 1])
            {
                // Removals before additions on a tie, so a replaced block reads as
                // the old text then the new one rather than interleaved.
                lines.Add(new DiffLine(DiffLineKind.Removed, left[start + x], start + x + 1, null));
                x++;
            }
            else
            {
                lines.Add(new DiffLine(DiffLineKind.Added, right[start + y], null, start + y + 1));
                y++;
            }
        }

        while (x < n)
        {
            lines.Add(new DiffLine(DiffLineKind.Removed, left[start + x], start + x + 1, null));
            x++;
        }

        while (y < m)
        {
            lines.Add(new DiffLine(DiffLineKind.Added, right[start + y], null, start + y + 1));
            y++;
        }
    }
}
