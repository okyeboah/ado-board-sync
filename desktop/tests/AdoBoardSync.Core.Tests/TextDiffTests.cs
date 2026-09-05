using AdoBoardSync.Core.Diff;

namespace AdoBoardSync.Core.Tests;

/// <summary>
/// The diff a reviewer accepts or rejects an agent's edit from (ABSD-704).
///
/// The assertions worth keeping are about what the reviewer can trust: an
/// unchanged line is reported unchanged rather than as a delete and an insert of
/// the same text, and every line carries the number it has in the file it came
/// from. A diff that renumbers or churns lines makes a reviewer scroll a long
/// backlog looking for the change they were told about.
/// </summary>
public class TextDiffTests
{
    private static IReadOnlyList<string> TextOf(TextDiffResult diff, DiffLineKind kind) =>
        [.. diff.Lines.Where(line => line.Kind == kind).Select(line => line.Text)];

    [Fact]
    public void TextComparedWithItselfHasNoChanges()
    {
        var diff = TextDiff.Between("one\ntwo\nthree\n", "one\ntwo\nthree\n");

        Assert.False(diff.HasChanges);
        Assert.Equal(0, diff.AddedCount);
        Assert.Equal(0, diff.RemovedCount);
        Assert.Equal(3, diff.Lines.Count);
        Assert.All(diff.Lines, line => Assert.Equal(DiffLineKind.Unchanged, line.Kind));
    }

    [Fact]
    public void AnInsertedLineIsTheOnlyLineReportedAsAdded()
    {
        var diff = TextDiff.Between("one\ntwo\n", "one\nmiddle\ntwo\n");

        Assert.Equal(["middle"], TextOf(diff, DiffLineKind.Added));
        Assert.Empty(TextOf(diff, DiffLineKind.Removed));
        Assert.Equal(["one", "two"], TextOf(diff, DiffLineKind.Unchanged));
    }

    [Fact]
    public void ADeletedLineIsTheOnlyLineReportedAsRemoved()
    {
        var diff = TextDiff.Between("one\nmiddle\ntwo\n", "one\ntwo\n");

        Assert.Equal(["middle"], TextOf(diff, DiffLineKind.Removed));
        Assert.Empty(TextOf(diff, DiffLineKind.Added));
    }

    [Fact]
    public void AReplacedLineReadsAsTheOldTextThenTheNew()
    {
        var diff = TextDiff.Between("one\nold\nthree\n", "one\nnew\nthree\n");

        Assert.Equal(
            [
                (DiffLineKind.Unchanged, "one"),
                (DiffLineKind.Removed, "old"),
                (DiffLineKind.Added, "new"),
                (DiffLineKind.Unchanged, "three"),
            ],
            diff.Lines.Select(line => (line.Kind, line.Text)));
    }

    [Fact]
    public void EveryLineCarriesItsNumberInTheFileItCameFrom()
    {
        var diff = TextDiff.Between("a\nb\nc\n", "a\nB1\nB2\nc\n");

        var removed = Assert.Single(diff.Lines, line => line.Kind == DiffLineKind.Removed);
        Assert.Equal(2, removed.OriginalLine);
        Assert.Null(removed.RevisedLine);

        var added = diff.Lines.Where(line => line.Kind == DiffLineKind.Added).ToList();
        Assert.Equal([2, 3], added.Select(line => line.RevisedLine));
        Assert.All(added, line => Assert.Null(line.OriginalLine));

        var last = diff.Lines[^1];
        Assert.Equal(DiffLineKind.Unchanged, last.Kind);
        Assert.Equal(3, last.OriginalLine);
        Assert.Equal(4, last.RevisedLine);
    }

    [Fact]
    public void MovingALineIsReportedAsOneRemovalAndOneAdditionRatherThanAWholeFileRewrite()
    {
        var diff = TextDiff.Between("a\nb\nc\nd\n", "b\nc\nd\na\n");

        Assert.Equal(["a"], TextOf(diff, DiffLineKind.Removed));
        Assert.Equal(["a"], TextOf(diff, DiffLineKind.Added));
        Assert.Equal(["b", "c", "d"], TextOf(diff, DiffLineKind.Unchanged));
    }

    [Fact]
    public void AnEmptyRevisionRemovesEveryLine()
    {
        var diff = TextDiff.Between("a\nb\n", string.Empty);

        Assert.Equal(["a", "b"], TextOf(diff, DiffLineKind.Removed));
        Assert.Equal(2, diff.RemovedCount);
        Assert.Equal(0, diff.AddedCount);
    }

    /// <summary>
    /// The parser leaves a form feed inside the line, so the diff has to as well —
    /// a diff that splits where the parser does not would point at a line number
    /// the editor cannot find.
    /// </summary>
    [Fact]
    public void LinesAreSplitTheWayTheParserSplitsThem()
    {
        var diff = TextDiff.Between("head\fstill the same line\n", "changed\fstill the same line\n");

        Assert.Equal(1, diff.RemovedCount);
        Assert.Equal(1, diff.AddedCount);
        Assert.Equal(["head\fstill the same line"], TextOf(diff, DiffLineKind.Removed));
    }

    [Fact]
    public void TheSummaryCountsWhatChangedInBothDirections()
    {
        var diff = TextDiff.Between("a\nb\n", "a\nB\nc\n");

        Assert.Equal("+2 −1", diff.Summary);
        Assert.False(diff.IsCoarse);
    }

    /// <summary>
    /// A rewrite too large to match line by line is still reported, as one block
    /// out and one block in. The bound is what stops an agent that rewrites a very
    /// large backlog from costing gigabytes of matching table.
    /// </summary>
    [Fact]
    public void ARewriteTooLargeToMatchIsReportedAsAWholeBlockReplacement()
    {
        var original = string.Join("\n", Enumerable.Range(0, 2100).Select(i => $"old line {i}"));
        var revised = string.Join("\n", Enumerable.Range(0, 2100).Select(i => $"new line {i}"));

        var diff = TextDiff.Between(original, revised);

        Assert.True(diff.IsCoarse);
        Assert.Equal(2100, diff.RemovedCount);
        Assert.Equal(2100, diff.AddedCount);
        Assert.Equal("old line 0", diff.Lines[0].Text);
        Assert.Equal(DiffLineKind.Removed, diff.Lines[0].Kind);
        Assert.Equal(DiffLineKind.Added, diff.Lines[^1].Kind);
    }

    [Fact]
    public void ASmallEditToALargeFileIsStillMatchedLineByLine()
    {
        var lines = Enumerable.Range(0, 20_000).Select(i => $"line {i}").ToList();
        var original = string.Join("\n", lines);
        lines[10_000] = "the agent changed this one";
        var revised = string.Join("\n", lines);

        var diff = TextDiff.Between(original, revised);

        Assert.False(diff.IsCoarse);
        Assert.Equal(1, diff.AddedCount);
        Assert.Equal(1, diff.RemovedCount);
    }
}
