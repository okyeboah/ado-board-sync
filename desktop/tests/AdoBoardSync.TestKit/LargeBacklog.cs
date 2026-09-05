using System.Text;

namespace AdoBoardSync.TestKit;

/// <summary>
/// Generates a large Markdown backlog for the tickets that measure the app at
/// scale (ABSD-107).
///
/// ABSD-203 (board virtualisation) and ABSD-205 (preview re-render latency) both
/// need a backlog far bigger than the committed fixtures, whose largest is 33
/// lines, and they need the <em>same</em> one: two benchmarks run against two
/// different backlogs produce two numbers that cannot be compared. It is
/// generated rather than committed because a backlog this size is unreviewable in
/// a diff — every unrelated edit to it would read as a change to the thing under
/// measurement — and because a generator can be asked for other sizes.
///
/// <para><b>The output is deterministic, and that is the point.</b> Nothing here
/// consults <see cref="Random" />, <see cref="DateTime" /> or <see cref="Guid" />:
/// every varying word is selected by arithmetic on the item's index, and lines are
/// terminated with a literal <c>\n</c> rather than <see cref="Environment.NewLine" />
/// so the bytes do not change with the host operating system. Two calls with the
/// same arguments return byte-identical text. A fixture that drifted between runs
/// would turn a latency benchmark into noise: the difference between two
/// measurements would be the fixture, not the code being measured.</para>
///
/// <para>The Markdown is shaped for <c>BacklogParser</c> under a default
/// configuration — <c>^##\s+(Epic\b.*)$</c> for Epics, <c>### PREFIX-123 · Title</c>
/// for Issues — and contains no stop heading, so it parses identically under
/// <see cref="InMemoryConfig" /> and <see cref="TempBoardProfile" />.</para>
/// </summary>
public static class LargeBacklog
{
    /// <summary>
    /// The number the first Issue code carries. Codes run consecutively from here
    /// across the whole backlog instead of restarting inside each Epic:
    /// <c>BacklogParser.TasksByCode</c> keys a dictionary by code, so any per-Epic
    /// scheme would throw on a duplicate key as soon as a caller asked for more
    /// issues per Epic than the scheme left room for.
    /// </summary>
    public const int FirstIssueNumber = 101;

    /// <summary>
    /// How many sections the oversized description carries. Nine puts it at roughly
    /// 200 lines, which is what makes it a worst case worth timing; every other
    /// Issue stays realistically sized so the average case is not distorted by it.
    /// </summary>
    private const int LargestDescriptionSections = 9;

    private static readonly string[] EpicThemes =
    [
        "Platform Foundations",
        "Delivery Pipeline",
        "Board Synchronisation",
        "Credential Handling",
        "Preview Rendering",
        "Operation History",
        "Diagnostics and Telemetry"
    ];

    private static readonly string[] EpicContexts =
    [
        "shared libraries every other layer depends on",
        "the path from a Markdown edit to an applied plan",
        "reconciling local codes against remote work items",
        "resolving a token without ever writing it down",
        "turning backlog Markdown into the HTML the board shows",
        "what was applied, when, and under which plan",
        "making a failed run explain itself"
    ];

    private static readonly string[] Verbs =
    [
        "Build", "Wire up", "Harden", "Port", "Instrument",
        "Cache", "Validate", "Retire", "Split", "Batch", "Trace"
    ];

    private static readonly string[] Subjects =
    [
        "the append-only event store",
        "the plan builder",
        "the backlog parser",
        "the credential resolver",
        "the preview converter",
        "the retry policy",
        "the CSV exporter",
        "the operation history",
        "the backlog file store",
        "the diff renderer",
        "the board connector",
        "the config loader",
        "the request throttle"
    ];

    private static readonly string[] CodeSpans =
    [
        "EventStore.Append",
        "PlanBuilder.Compute",
        "BacklogParser.Parse",
        "PatResolver.Resolve",
        "MarkdownConverter.ToHtml",
        "RetryPolicy.Backoff",
        "CsvExporter.Write"
    ];

    private static readonly string[] BoldWords =
    [
        "idempotent", "atomic", "ordered", "cancellable", "resumable", "bounded"
    ];

    private static readonly string[] ItalicWords =
    [
        "never re-entrant",
        "safe to retry",
        "observed under load",
        "measured rather than guessed",
        "pure"
    ];

    private static readonly string[] Constraints =
    [
        "The write path holds no lock across an await",
        "A partial apply is reported, never swallowed",
        "The plan is computed before anything is sent",
        "Every remote failure carries the code that caused it"
    ];

    private static readonly string[] Details =
    [
        "verified by the parity suite",
        "asserted against the Python reference",
        "covered by the snapshot policy above",
        "the stamp moves on every write",
        "the cancellation token reaches the socket"
    ];

    private static readonly string[] Meanings =
    [
        "the identity carried across a retry",
        "the state the board will show",
        "what a rerun is allowed to change",
        "the field the exporter reads",
        "the value compared against the remote item",
        "the reason a row was skipped",
        "the stamp the next read must match"
    ];

    /// <summary>
    /// Renders the backlog: exactly <paramref name="issues" /> Issue headings, in
    /// runs of <paramref name="issuesPerEpic" /> under <see cref="ExpectedEpicCount" />
    /// Epic headings.
    /// </summary>
    public static string Generate(string codePrefix = "PROJ", int issues = 500, int issuesPerEpic = 20)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codePrefix);
        ArgumentOutOfRangeException.ThrowIfNegative(issues);
        ArgumentOutOfRangeException.ThrowIfLessThan(issuesPerEpic, 1);

        var builder = new StringBuilder();
        Line(builder, "# Generated backlog");
        Blank(builder);
        Line(builder, "Prose before the first Epic heading is ignored by the parser.");
        Blank(builder);

        for (var index = 0; index < issues; index++)
        {
            if (index % issuesPerEpic == 0)
            {
                AppendEpicHeading(builder, index / issuesPerEpic);
            }

            AppendIssue(builder, codePrefix, index);
        }

        // One trailing newline, never two. The blank line that separates Issues
        // would otherwise leave the last Issue with a trailing description line no
        // other Issue has — exactly the off-by-one a golden-file comparison trips on.
        return builder.ToString().TrimEnd('\n') + "\n";
    }

    /// <summary>
    /// How many Epic headings <see cref="Generate" /> emits for the same arguments.
    /// Exposed so a caller asserts against the generator's own arithmetic rather
    /// than a magic number, which would silently stop matching the day the default
    /// size changes.
    /// </summary>
    public static int ExpectedEpicCount(int issues = 500, int issuesPerEpic = 20)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(issues);
        ArgumentOutOfRangeException.ThrowIfLessThan(issuesPerEpic, 1);

        return (issues + issuesPerEpic - 1) / issuesPerEpic;
    }

    /// <summary>
    /// The code of the single Issue carrying the deliberately oversized
    /// description — ABSD-205's worst case for "re-render the largest description".
    ///
    /// A method rather than the <c>const</c> the name suggests, because the code
    /// embeds <paramref name="codePrefix" />. A const is fixed at compile time, so
    /// it could only ever spell the default prefix, and would quietly name a
    /// non-existent Issue for any caller generating under a different one.
    /// </summary>
    public static string LargestIssueCode(string codePrefix = "PROJ") => IssueCode(codePrefix, 0);

    /// <summary>
    /// The code of the Issue at <paramref name="index" /> in generation order, so a
    /// scrolling or virtualisation test can name a row far down the backlog without
    /// parsing the whole document first.
    /// </summary>
    public static string IssueCode(string codePrefix, int index)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codePrefix);
        ArgumentOutOfRangeException.ThrowIfNegative(index);

        return $"{codePrefix}-{FirstIssueNumber + index}";
    }

    private static void AppendEpicHeading(StringBuilder builder, int epic)
    {
        Line(builder, $"## Epic {epic + 1} — {Pick(EpicThemes, epic)}");
        Line(builder, $"*Context: {Pick(EpicContexts, epic)}.*");
        Blank(builder);
    }

    private static void AppendIssue(StringBuilder builder, string codePrefix, int index)
    {
        Line(builder, $"### {IssueCode(codePrefix, index)} · {Pick(Verbs, index)} {Pick(Subjects, index + 1)}");

        if (index == 0)
        {
            AppendLargestDescription(builder);
        }
        else
        {
            AppendDescription(builder, index);
        }

        AppendTasks(builder, index);
        Blank(builder);
    }

    /// <summary>
    /// The constructs the preview and the converter actually branch on — inline
    /// bold, italics and code, a nested list, a table with a header and separator
    /// row, and a rule — so a render benchmark exercises every path instead of one
    /// long paragraph.
    ///
    /// Nested items use <c>*</c>, never <c>-</c>: the parser reads any line whose
    /// <em>stripped</em> form starts with <c>"- "</c> as a Task, indented or not, so
    /// a dash here would inflate the Task count of every Issue.
    /// </summary>
    private static void AppendDescription(StringBuilder builder, int index)
    {
        var subject = Pick(Subjects, index);
        var codeSpan = Pick(CodeSpans, index);
        var bold = Pick(BoldWords, index);
        var italic = Pick(ItalicWords, index);

        Line(builder, $"*Reference: ADR-{100 + (index % 47):D3}.*");
        Blank(builder);
        Line(builder, $"Work on {subject} must stay **{bold}** across a restart, which is");
        Line(builder, $"*{italic}* only while `{codeSpan}` owns the write. The table below");
        Line(builder, "names the fields the converter reads.");
        Blank(builder);
        Line(builder, $"* {Pick(Constraints, index)}");
        Line(builder, $"  * {Pick(Details, index)}");
        Line(builder, $"  * {Pick(Details, index + 1)}");
        Line(builder, $"* {Pick(Constraints, index + 1)}");
        Line(builder, $"  * {Pick(Details, index + 2)}");
        Blank(builder);
        Line(builder, "| Field | Meaning |");
        Line(builder, "| --- | --- |");
        Line(builder, $"| `{codeSpan}` | {Pick(Meanings, index)} |");
        Line(builder, $"| **{bold}** | {Pick(Meanings, index + 1)} |");
        Blank(builder);
        Line(builder, "---");
        Blank(builder);
    }

    private static void AppendLargestDescription(StringBuilder builder)
    {
        Line(builder, "*Reference: the worst case the preview must still render inside a frame.*");
        Blank(builder);
        Line(builder, "This Issue carries a **deliberately oversized** description so that a");
        Line(builder, "re-render benchmark has a real *worst case* to measure, instead of a page");
        Line(builder, "that fits on `one screen` and finishes before the timer resolves.");
        Blank(builder);

        for (var section = 0; section < LargestDescriptionSections; section++)
        {
            Line(builder, $"**Section {section + 1} — {Pick(EpicThemes, section)}**");
            Blank(builder);
            Line(builder, $"Handling {Pick(Subjects, section)} is *{Pick(ItalicWords, section)}*, so a");
            Line(builder, $"re-render must not re-parse the whole document: `{Pick(CodeSpans, section)}` is");
            Line(builder, $"**{Pick(BoldWords, section)}** and the preview reuses what it produced.");
            Blank(builder);
            Line(builder, $"* {Pick(Constraints, section)}");
            Line(builder, $"  * {Pick(Details, section)}");
            Line(builder, $"  * {Pick(Details, section + 1)}");
            Line(builder, $"* {Pick(Constraints, section + 1)}");
            Line(builder, $"  * {Pick(Details, section + 2)}");
            Line(builder, $"  * {Pick(Details, section + 3)}");
            Blank(builder);
            Line(builder, "| Field | Meaning | Owner |");
            Line(builder, "| --- | --- | --- |");
            Line(builder, $"| `{Pick(CodeSpans, section)}` | {Pick(Meanings, section)} | {Pick(Subjects, section)} |");
            Line(builder, $"| **{Pick(BoldWords, section)}** | {Pick(Meanings, section + 1)} | {Pick(Subjects, section + 1)} |");
            Line(builder, $"| *{Pick(ItalicWords, section)}* | {Pick(Meanings, section + 2)} | {Pick(Subjects, section + 2)} |");
            Line(builder, $"| {Pick(BoldWords, section + 1)} | {Pick(Meanings, section + 3)} | {Pick(Subjects, section + 3)} |");
            Blank(builder);
            Line(builder, "---");
            Blank(builder);
        }
    }

    /// <summary>
    /// Two to four Tasks per Issue, cycling by index. These are the only lines in
    /// the whole document whose stripped form starts with <c>"- "</c>, so the Task
    /// count a caller asserts is exactly the count emitted here.
    /// </summary>
    private static void AppendTasks(StringBuilder builder, int index)
    {
        var count = 2 + (index % 3);
        for (var task = 0; task < count; task++)
        {
            Line(builder, $"- {Pick(Verbs, index + task)} {Pick(Subjects, index + (task * 3))}");
        }
    }

    private static string Pick(string[] values, int index) => values[index % values.Length];

    private static void Line(StringBuilder builder, string text) => builder.Append(text).Append('\n');

    private static void Blank(StringBuilder builder) => builder.Append('\n');
}
