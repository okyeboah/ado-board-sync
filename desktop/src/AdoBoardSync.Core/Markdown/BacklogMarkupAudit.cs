using AdoBoardSync.Core.Backlog;

namespace AdoBoardSync.Core.Markdown;

/// <summary>
///     One malformed-markup finding, scoped the way the CLI's <c>check-html</c> scopes
///     it: each description as a whole, and each Task bullet separately under a
///     "task &lt;first 40 chars&gt;" label. Nothing here reads Azure DevOps; this is the
///     offline half of both the editor's inline flags and Apply's markup gate.
/// </summary>
public static class BacklogMarkupAudit
{
    /// <summary>The audit runs on generated markup, never on authored source.</summary>
    public static IReadOnlyList<(string Scope, string Message)> ProblemsFor(BacklogItem item)
    {
        var html = MarkdownHtml.ToHtml(item.DescriptionLines);

        var problems = new List<(string, string)>();
        problems.AddRange(HtmlBalance.Problems(html).Select(p => ("description", p)));

        foreach (var bullet in item.Bullets)
        {
            var plain = MarkdownHtml.Plain(bullet);
            var label = plain.Length > 40 ? plain[..40] : plain;
            problems.AddRange(HtmlBalance
                .Problems(MarkdownHtml.Inline(bullet))
                .Select(p => ($"task {label}", p)));
        }

        return problems;
    }

    public static int Total(IEnumerable<BacklogItem> items)
    {
        return items.Sum(item => ProblemsFor(item).Count);
    }
}
