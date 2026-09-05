using System.Text.RegularExpressions;

namespace AdoBoardSync.Core;

/// <summary>
/// Python semantics the port depends on, in one place.
///
/// The CLI is the reference implementation, so several of its behaviours are
/// load-bearing rather than incidental. Each one lived as a private copy in the
/// module that first needed it; every further module ported from Python needs the
/// same rules, so they are stated once here. Public because the desktop layer
/// splits editor buffers with the same rule the parser applies to files.
/// </summary>
public static class PythonCompat
{
    /// <summary>
    /// Applies <c>re.match</c> semantics: anchored at position 0, not a search.
    ///
    /// .NET's <see cref="Regex.Match(string)"/> searches, and returns the leftmost
    /// match, so requiring <c>Index == 0</c> is equivalent. This matters wherever a
    /// caller-supplied pattern is used — notably the configurable epic heading
    /// regex, which need not start with <c>^</c>.
    /// </summary>
    public static bool MatchAtStart(Regex regex, string input, out Match match)
    {
        match = regex.Match(input);
        return match.Success && match.Index == 0;
    }

    public static bool MatchAtStart(Regex regex, string input) =>
        MatchAtStart(regex, input, out _);

    /// <summary>
    /// Splits text into lines the way Python's text-mode file iteration does.
    ///
    /// <see cref="StringReader"/> matches it exactly: it breaks on CR, LF, and CRLF
    /// and yields no trailing empty line for text ending in a newline. Note that
    /// <c>string.ReplaceLineEndings</c> is <em>not</em> equivalent — it also breaks
    /// on FF, NEL, LS, and PS, which Python's text mode leaves inside the line.
    /// </summary>
    public static IEnumerable<string> SplitLines(string text)
    {
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }
}
