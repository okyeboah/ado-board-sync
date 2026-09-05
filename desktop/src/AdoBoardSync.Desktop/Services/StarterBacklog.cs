using AdoBoardSync.Core.Backlog;
using AdoBoardSync.Core.Results;

namespace AdoBoardSync.Desktop.Services;

/// <summary>
/// The starter backlog written when onboarding names a backlog file that does not
/// exist yet, so a brand-new organization can go from an empty directory to an
/// open profile in one pass. The content parses with the form's own code prefix —
/// it is a working backlog, not a placeholder comment.
/// </summary>
public static class StarterBacklog
{
    public static string Content(string codePrefix)
    {
        // Verbatim, never re-cased: the config's code_prefix is matched
        // case-sensitively against the heading, so a lowercase prefix must
        // produce lowercase headings for the starter to parse as Issues.
        var prefix = string.IsNullOrWhiteSpace(codePrefix) ? "PROJ" : codePrefix.Trim();

        return $""""
            # {prefix} backlog

            ## Epic 1 — First epic

            *What this epic is for.*

            ### {prefix}-101 · First issue

            Describe the work here. **bold**, *italics*, `code`, bullet lists and
            pipe tables all render on the board exactly as Azure DevOps will show them.

            | Field | Meaning |
            | --- | --- |
            | code | the stable identity, {prefix}-101 |

            - A first task
            - A second task
            """";
    }

    public static Result<string> Write(IBacklogFileStore store, string path, string codePrefix)
    {
        var written = store.WriteAtomic(path, Content(codePrefix));
        return written.IsFailure ? written.Error! : path;
    }
}
