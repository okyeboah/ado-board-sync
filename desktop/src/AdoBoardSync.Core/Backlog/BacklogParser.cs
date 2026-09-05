using AdoBoardSync.Core.Configuration;

namespace AdoBoardSync.Core.Backlog;

/// <summary>
/// Parses a Markdown backlog into an ordered Epic / Issue / Task model.
///
/// A port of the CLI's <c>parser.py</c>. Backlog shape, configurable through the
/// config's regexes and code prefix:
/// <code>
/// ## Epic 1 — Title            -> Epic
/// *italic context lines*       -> part of the Epic description
/// ### PROJ-101 · Title         -> Issue (code = PROJ-101, prefix "PROJ")
/// - top-level bullet           -> a child Task of that Issue
///   * nested bullet            -> part of the Issue description only
/// </code>
/// Everything between a heading and the next heading is that item's description.
/// Parsing starts at the first Epic heading and stops at any stop-heading entry.
///
/// The parser takes the backlog as text, never a path: the editor re-parses an
/// unsaved buffer on every keystroke, and reading files is the profile layer's job.
/// </summary>
public static class BacklogParser
{
    public static IReadOnlyList<BacklogItem> Parse(BoardConfig config, string markdown)
    {
        var items = new List<BacklogItem>();
        var description = new List<string>();
        var started = false;

        // Where the current item's description block begins, or -1 while it has
        // no description lines. Leading blanks are skipped, so the block starts
        // at the first description line, not at the heading.
        var descriptionStart = -1;
        var lineIndex = 0;

        void Finalize()
        {
            if (items.Count > 0)
            {
                var target = items[^1];
                items[^1] = target with
                {
                    // The block ends where the line that ended it begins: the next
                    // heading, a stop heading, or the end of the file. An item with
                    // no description starts and ends there, so an edit that adds
                    // one splices in directly after the skipped blanks.
                    DescriptionStart = descriptionStart < 0 ? lineIndex : descriptionStart,
                    DescriptionEnd = lineIndex,
                    DescriptionLines = description,
                    Bullets = BulletsOf(target.Level, description)
                };
            }

            description = [];
            descriptionStart = -1;
        }

        var lines = PythonCompat.SplitLines(markdown).ToList();
        for (lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex];
            var stripped = line.Trim();

            if (StartsWithAnyStopHeading(config, stripped))
            {
                break;
            }

            if (PythonCompat.MatchAtStart(config.EpicHeadingRegex, stripped, out var epic))
            {
                Finalize();
                items.Add(new BacklogItem
                {
                    Level = BacklogLevel.Epic,
                    Title = epic.Groups[1].Value.Trim()
                });
                started = true;
                continue;
            }

            if (!started)
            {
                continue;
            }

            if (stripped.StartsWith("### ", StringComparison.Ordinal))
            {
                var code = config.IssueCodeRegex.Match(stripped);
                if (code.Success)
                {
                    Finalize();
                    items.Add(new BacklogItem
                    {
                        Level = BacklogLevel.Issue,
                        Code = code.Groups[1].Value.ToUpperInvariant(),
                        Title = stripped.TrimStart('#').Trim()
                    });
                    continue;
                }
            }

            // Description line for the current item (skip leading blanks).
            if (started)
            {
                if (description.Count == 0 && stripped.Length == 0)
                {
                    continue;
                }

                if (description.Count == 0)
                {
                    descriptionStart = lineIndex;
                }

                description.Add(line);
            }
        }

        Finalize();
        return items;
    }

    /// <summary>Maps each Issue code to its list of top-level bullet strings (Tasks).</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> TasksByCode(
        BoardConfig config,
        string markdown) =>
        Parse(config, markdown)
            .Where(item => item.Level == BacklogLevel.Issue)
            .ToDictionary(item => item.Code!, item => item.Bullets);

    /// <summary>
    /// The Task bullets an item's description block yields: top-level <c>-</c>
    /// lines, stripped. Stated once here because the parser and the live editor
    /// must derive the same Tasks from the same lines.
    /// </summary>
    public static IReadOnlyList<string> BulletsOf(BacklogLevel level, IReadOnlyList<string> descriptionLines) =>
        level == BacklogLevel.Issue
            ? [.. descriptionLines
                .Where(line => line.Trim().StartsWith("- ", StringComparison.Ordinal))
                .Select(line => line.Trim()[2..].Trim())]
            : [];

    // An indexed loop rather than Any(predicate): this runs on every line of every
    // keystroke re-parse, and the closure plus interface enumerator would allocate
    // twice per line for a list that is usually empty.
    private static bool StartsWithAnyStopHeading(BoardConfig config, string stripped)
    {
        for (var i = 0; i < config.StopHeadings.Count; i++)
        {
            if (stripped.StartsWith(config.StopHeadings[i], StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
