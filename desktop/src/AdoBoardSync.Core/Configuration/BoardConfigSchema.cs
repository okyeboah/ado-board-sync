using System.Text.Json;
using System.Text.RegularExpressions;
using AdoBoardSync.Core.Results;

namespace AdoBoardSync.Core.Configuration;

/// <summary>
/// Checks a parsed <c>board.config.json</c> against the constraints in
/// <c>board.config.schema.json</c>.
///
/// The schema documents rules that neither the CLI nor a plain deserialize
/// enforces, so a config could satisfy both and still be wrong. The two that bite
/// hardest are a mistyped key, which System.Text.Json drops in silence — the
/// setting then appears to have no effect for reasons nothing reports — and a
/// numeric floor, where a <c>timeout</c> of 0 fails only later, against the board.
///
/// This mirrors the schema rather than interpreting it: no JSON Schema engine, and
/// therefore no dependency, but the two must be changed together. The property
/// list is asserted against the schema file by
/// <c>BoardConfigSchemaTests.EveryPropertyInTheSchemaFileIsKnownHere</c>, so
/// adding a key to the schema without adding it here fails the build.
/// </summary>
public static partial class BoardConfigSchema
{
    /// <summary>
    /// Every key the schema allows. The schema sets <c>additionalProperties: false</c>,
    /// so anything absent from this set is rejected rather than ignored.
    /// </summary>
    public static readonly IReadOnlySet<string> KnownProperties = new HashSet<string>(StringComparer.Ordinal)
    {
        "org", "project", "code_prefix", "board_file", "csv_file", "types", "states",
        "epic_heading_regex", "stop_headings", "api_version", "pat_env", "pat_file",
        "task_title_max", "max_retries", "backoff", "timeout", "team", "iterations", "assignees"
    };

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9]*$")]
    private static partial Regex CodePrefixPattern { get; }

    /// <summary>
    /// Returns the first violation, or null when the document satisfies the schema.
    /// Validation runs on the raw document because a mistyped key does not survive
    /// deserialization into a typed object.
    /// </summary>
    public static Error? Validate(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return Error.Validation("config.invalid_json", $"board.config.json is not valid JSON: {ex.Message}");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Error.Validation("config.not_an_object", "board.config.json must contain a JSON object.");
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!KnownProperties.Contains(property.Name))
                {
                    return Error.Validation(
                        "config.unknown_key",
                        $"board.config.json has an unknown key '{property.Name}'. "
                        + $"Did you mean {NearestKnown(property.Name)}?");
                }
            }

            return ValidateValues(document.RootElement);
        }
    }

    private static Error? ValidateValues(JsonElement root)
    {
        if (root.TryGetProperty("code_prefix", out var prefix)
            && prefix.ValueKind == JsonValueKind.String
            && !CodePrefixPattern.IsMatch(prefix.GetString() ?? string.Empty))
        {
            return Error.Validation(
                "config.bad_code_prefix",
                $"code_prefix '{prefix.GetString()}' must start with a letter and contain only letters and digits.");
        }

        foreach (var (name, minimum) in new (string, double)[]
                 {
                     ("task_title_max", 1),
                     ("max_retries", 0),
                     ("backoff", 0),
                     ("timeout", 1)
                 })
        {
            if (root.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.GetDouble() < minimum)
            {
                return Error.Validation(
                    "config.below_minimum",
                    $"{name} is {value.GetDouble()}, but the minimum is {minimum}.");
            }
        }

        return ValidateIterations(root);
    }

    private static Error? ValidateIterations(JsonElement root)
    {
        if (!root.TryGetProperty("iterations", out var iterations)
            || iterations.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var index = 0;
        foreach (var iteration in iterations.EnumerateArray())
        {
            if (iteration.ValueKind != JsonValueKind.Object
                || !iteration.TryGetProperty("name", out var name)
                || name.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(name.GetString()))
            {
                return Error.Validation(
                    "config.iteration_without_name",
                    $"iterations[{index}] must set a non-empty 'name'.");
            }

            index++;
        }

        return null;
    }

    /// <summary>
    /// The closest known key by edit distance, to make a typo self-correcting. Falls
    /// back to naming nothing rather than suggesting something unrelated.
    /// </summary>
    private static string NearestKnown(string unknown)
    {
        var best = KnownProperties
            .Select(known => (known, distance: EditDistance(unknown.ToLowerInvariant(), known)))
            .OrderBy(candidate => candidate.distance)
            .First();

        // Beyond a third of the length the "suggestion" is noise, not help.
        return best.distance <= Math.Max(2, unknown.Length / 3)
            ? $"'{best.known}'"
            : "one of the keys in board.config.schema.json";
    }

    private static int EditDistance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var substitution = previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
