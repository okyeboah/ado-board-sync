using AdoBoardSync.Core.Backlog;
using AdoBoardSync.Core.Configuration;

namespace AdoBoardSync.Core.Csv;

/// <summary>
/// The import CSV: parsed backlog to Azure DevOps' "Import work items" layout.
///
/// A port of the CLI's <c>csvio.py</c> serialization — same four columns, and the
/// same quoting the Python <c>csv</c> module's default (excel) dialect produces:
/// quote only when the field contains a delimiter, a quote character, or a line
/// break; double the quote character inside; terminate every record with CRLF.
/// Epics carry their title in <c>Title 1</c>, Issues in <c>Title 2</c>, so the
/// web importer can nest them. The CSV is an artifact for review and for the ADO
/// web importer — Plans are computed from the backlog, never from this file.
/// </summary>
public static class ImportCsv
{
    public static IReadOnlyList<string> FieldNames { get; } =
        ["Work Item Type", "Title 1", "Title 2", "Description"];

    public static string Serialize(BoardConfig config, IReadOnlyList<BacklogItem> items)
    {
        var builder = new System.Text.StringBuilder();
        AppendRecord(builder, FieldNames);

        foreach (var item in items)
        {
            var description = Markdown.MarkdownHtml.ToHtml(item.DescriptionLines);
            AppendRecord(
                builder,
                [
                    item.Level == BacklogLevel.Epic ? config.Types["epic"] : config.Types["story"],
                    item.Level == BacklogLevel.Epic ? item.Title : string.Empty,
                    item.Level == BacklogLevel.Epic ? string.Empty : item.Title,
                    description,
                ]);
        }

        return builder.ToString();
    }

    private static void AppendRecord(System.Text.StringBuilder builder, IReadOnlyList<string> fields)
    {
        for (var i = 0; i < fields.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(Quote(fields[i]));
        }

        builder.Append("\r\n");
    }

    private static string Quote(string field)
    {
        var needsQuotes = field.Contains(',') || field.Contains('"') || field.Contains('\r') || field.Contains('\n');
        return needsQuotes ? $"\"{field.Replace("\"", "\"\"")}\"" : field;
    }
}
