using System.Collections.ObjectModel;
using System.Text;
using System.Text.RegularExpressions;

namespace AdoBoardSync.Core.Diagnostics;

/// <summary>
/// Strips secrets out of a <see cref="DiagnosticEvent"/> before anything persists
/// or exports it (ABSD-507). Nothing here touches the filesystem or the clock, so
/// the rule it enforces can be proved by reading it rather than by running the app.
///
/// Three passes, in order of how much they can be trusted:
///
/// 1. <b>Registered values.</b> The composition root registers the PAT the moment
///    it resolves one, so the token is caught by identity rather than by looking
///    like a token. This is the half that works when a PAT does not match any
///    shape we anticipated — a future Azure DevOps token format, or a password a
///    user typed into the wrong box.
/// 2. <b>Key names.</b> A <see cref="DiagnosticEvent.Data"/> value under a key that
///    reads like a credential is dropped whole, whatever it holds.
/// 3. <b>Shape.</b> A backstop for a value that reached a diagnostic without ever
///    being registered.
///
/// A match is replaced by <see cref="Placeholder"/> in full. It is never shortened
/// to a prefix: a token's first eight characters are enough to identify which token
/// leaked and to narrow a brute force, so "safe" truncation is still a leak.
///
/// <para>
/// The registered-value pass is therefore stated as a run guarantee rather than a
/// whole-value one: <b>no run of <see cref="AnchorLength"/> or more consecutive
/// characters of a registered value survives</b>. A caller that trimmed a secret
/// itself before logging it — <c>token[..12]</c>, the "safe" logging that is not —
/// is caught by the same pass as the whole value.
/// </para>
/// </summary>
public sealed partial class DiagnosticRedaction
{
    public const string Placeholder = "[redacted]";

    /// <summary>
    /// How much of a registered secret has to appear before the pass treats it as a
    /// leak. Short enough to catch a caller's own truncation, long enough that an
    /// ordinary run of prose cannot collide with part of a real token.
    /// </summary>
    private const int AnchorLength = 8;

    /// <summary>
    /// Registering something shorter than this would turn a common substring into a
    /// redaction rule and empty every message. A credential this short is not one
    /// this application can protect.
    /// </summary>
    private const int ShortestRegisterableSecret = 4;

    // Matched against the key with punctuation removed, so "api_key", "Api-Key" and
    // "apiKey" are one rule rather than three.
    private static readonly string[] SecretKeyFragments =
    [
        "token", "password", "secret", "authorization", "credential", "apikey", "bearer",
    ];

    // Matched against whole segments of the key, never as substrings. "pat" is why:
    // as a substring it also matches "path", which is the one field that makes a
    // file-write event traceable, and "patch", which names half the connector's
    // calls. A rule that redacts those buys nothing and costs the log its value.
    private static readonly string[] SecretKeyWords = ["pat", "auth", "pwd"];

    private readonly Lock _gate = new();

    // Longest first: when one registered value contains another, replacing the
    // longer one first leaves no tail of it behind under a shorter match.
    private readonly List<RegisteredSecret> _secrets = [];

    /// <summary>
    /// Registers a value that must never appear in a diagnostic again. Safe to call
    /// with the same value twice, and safe to call from any thread — an Apply run
    /// resolves its PAT while its worker tasks are already writing events.
    /// </summary>
    public void Register(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret) || secret.Length < ShortestRegisterableSecret)
        {
            return;
        }

        lock (_gate)
        {
            if (_secrets.Exists(known => string.Equals(known.Value, secret, StringComparison.Ordinal)))
            {
                return;
            }

            _secrets.Add(RegisteredSecret.For(secret, AnchorLength));
            _secrets.Sort(static (left, right) => right.Value.Length.CompareTo(left.Value.Length));
        }
    }

    /// <summary>
    /// How many values are registered. Exposed so a test can prove that registering
    /// something too short to protect was ignored rather than silently accepted.
    /// </summary>
    public int RegisteredCount
    {
        get
        {
            lock (_gate)
            {
                return _secrets.Count;
            }
        }
    }

    /// <summary>
    /// The event as it is safe to persist. Returns the same instance when there was
    /// nothing to remove, so the ordinary case allocates nothing.
    /// </summary>
    public DiagnosticEvent Apply(DiagnosticEvent diagnosticEvent)
    {
        ArgumentNullException.ThrowIfNull(diagnosticEvent);

        var message = Redact(diagnosticEvent.Message);
        var changed = !string.Equals(message, diagnosticEvent.Message, StringComparison.Ordinal);

        Dictionary<string, string>? data = null;
        if (diagnosticEvent.Data.Count > 0)
        {
            data = new Dictionary<string, string>(diagnosticEvent.Data.Count, StringComparer.Ordinal);
            foreach (var entry in diagnosticEvent.Data)
            {
                var value = IsSecretKey(entry.Key) ? Placeholder : Redact(entry.Value);
                changed |= !string.Equals(value, entry.Value, StringComparison.Ordinal);
                data[entry.Key] = value;
            }
        }

        if (!changed)
        {
            return diagnosticEvent;
        }

        // Category and Code are left alone on purpose. They come from the fixed
        // FSD §5.1 vocabulary the status bar already showed the user, so running a
        // pattern over them can only corrupt the one field that makes a support
        // conversation start from the same word the user saw.
        return diagnosticEvent with
        {
            Message = message,
            Data = data is null
                ? diagnosticEvent.Data
                : new ReadOnlyDictionary<string, string>(data),
        };
    }

    /// <summary>Removes every registered value, then every recognised shape, from one string.</summary>
    public string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        RegisteredSecret[] secrets;
        lock (_gate)
        {
            secrets = [.. _secrets];
        }

        var result = text;
        foreach (var secret in secrets)
        {
            result = RemoveSecret(result, secret);
        }

        return RemoveKnownShapes(result);
    }

    /// <summary>
    /// A registered value together with every window of it the scan looks for. The
    /// windows are built once at registration rather than per event: an Apply run
    /// writes an event per row, and rebuilding them each time would put the cost of
    /// the guarantee on the hot path.
    /// </summary>
    private sealed record RegisteredSecret(string Value, int WindowWidth, HashSet<string> Windows)
    {
        public static RegisteredSecret For(string value, int anchorLength)
        {
            // A value shorter than the anchor has exactly one window — itself — so a
            // short registered password is still matched, just only in full.
            var width = Math.Min(anchorLength, value.Length);
            var windows = new HashSet<string>(StringComparer.Ordinal);
            for (var start = 0; start + width <= value.Length; start++)
            {
                windows.Add(value.Substring(start, width));
            }

            return new RegisteredSecret(value, width, windows);
        }
    }

    /// <summary>
    /// True when a <see cref="DiagnosticEvent.Data"/> key reads like a credential, so
    /// its value goes whatever it contains. Exposed because the rule is worth
    /// asserting directly: whether "path" is a secret key is not obvious from the
    /// word list.
    /// </summary>
    public static bool IsSecretKey(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        var normalized = Normalize(key);
        foreach (var fragment in SecretKeyFragments)
        {
            if (normalized.Contains(fragment, StringComparison.Ordinal))
            {
                return true;
            }
        }

        foreach (var segment in Segments(key))
        {
            foreach (var word in SecretKeyWords)
            {
                if (string.Equals(segment, word, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Replaces every run of <paramref name="secret"/> in <paramref name="text"/>,
    /// whole or partial.
    ///
    /// The scan matches against the secret's set of fixed-width windows rather than
    /// against the secret itself, which is what makes a partial leak — a prefix, a
    /// tail, a slice out of the middle — the same case as the whole value instead of
    /// a case someone has to have anticipated. A run is extended for as long as each
    /// successive window still belongs to the secret, so the replacement covers
    /// exactly what leaked and leaves no fragment either side of it.
    /// </summary>
    private static string RemoveSecret(string text, RegisteredSecret secret)
    {
        var width = secret.WindowWidth;
        if (text.Length < width)
        {
            return text;
        }

        var windows = secret.Windows.GetAlternateLookup<ReadOnlySpan<char>>();

        StringBuilder? builder = null;
        var copiedTo = 0;
        var index = 0;

        while (index + width <= text.Length)
        {
            if (!windows.Contains(text.AsSpan(index, width)))
            {
                index++;
                continue;
            }

            var end = index + width;
            while (end < text.Length && windows.Contains(text.AsSpan(end - width + 1, width)))
            {
                end++;
            }

            builder ??= new StringBuilder(text.Length);
            builder.Append(text, copiedTo, index - copiedTo).Append(Placeholder);

            copiedTo = end;
            index = end;
        }

        if (builder is null)
        {
            return text;
        }

        builder.Append(text, copiedTo, text.Length - copiedTo);
        return builder.ToString();
    }

    private static string RemoveKnownShapes(string text)
    {
        // Scheme-qualified forms first, so what survives reads "Bearer [redacted]"
        // rather than a bare placeholder that hides which header carried the value.
        var result = AuthorizationHeaderValue().Replace(text, $"$1 {Placeholder}");
        result = UrlUserInfoPassword().Replace(result, $"$1:{Placeholder}");
        result = ClassicAzureDevOpsPat().Replace(result, Placeholder);
        return OpaqueMixedCaseToken().Replace(result, Placeholder);
    }

    /// <summary>
    /// A classic Azure DevOps PAT: 52 characters of lowercase base32. No hash this
    /// application produces is 52 characters long, so the length alone separates it
    /// from a backlog or board fingerprint.
    /// </summary>
    [GeneratedRegex(@"\b[a-z2-7]{52}\b")]
    private static partial Regex ClassicAzureDevOpsPat();

    /// <summary>
    /// The backstop for a longer, mixed-case token. The two lookaheads require both
    /// an uppercase letter and a digit, which is what keeps it off the lowercase hex
    /// fingerprints in a stale-plan diagnostic — losing those would blind the one
    /// report that needs them.
    /// </summary>
    [GeneratedRegex(@"\b(?=[A-Za-z0-9_-]*[A-Z])(?=[A-Za-z0-9_-]*[0-9])[A-Za-z0-9_-]{32,}\b")]
    private static partial Regex OpaqueMixedCaseToken();

    [GeneratedRegex(@"\b(Basic|Bearer)\s+[A-Za-z0-9+/=._-]{8,}", RegexOptions.IgnoreCase)]
    private static partial Regex AuthorizationHeaderValue();

    /// <summary>
    /// The password half of <c>https://user:pat@dev.azure.com/...</c>. Pasting a
    /// clone URL is how an Azure DevOps PAT most often ends up somewhere it was
    /// never typed.
    /// </summary>
    [GeneratedRegex(@"(?<=://)([^\s/:@]+):[^\s/@]+(?=@)")]
    private static partial Regex UrlUserInfoPassword();

    private static string Normalize(string key)
    {
        var builder = new StringBuilder(key.Length);
        foreach (var character in key)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Splits a key into words at punctuation and at a lower-to-upper transition, so
    /// "pat_env", "patFile" and "PAT" all yield the segment "pat" while "path" stays
    /// one segment that matches nothing.
    /// </summary>
    private static List<string> Segments(string key)
    {
        var segments = new List<string>();
        var current = new StringBuilder(key.Length);

        for (var i = 0; i < key.Length; i++)
        {
            var character = key[i];
            if (!char.IsLetterOrDigit(character))
            {
                Flush(segments, current);
                continue;
            }

            if (i > 0 && char.IsUpper(character) && char.IsLower(key[i - 1]))
            {
                Flush(segments, current);
            }

            current.Append(char.ToLowerInvariant(character));
        }

        Flush(segments, current);
        return segments;

        static void Flush(List<string> into, StringBuilder current)
        {
            if (current.Length > 0)
            {
                into.Add(current.ToString());
                current.Clear();
            }
        }
    }
}
