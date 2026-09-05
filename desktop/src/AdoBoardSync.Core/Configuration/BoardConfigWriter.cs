using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using AdoBoardSync.Core.Results;

namespace AdoBoardSync.Core.Configuration;

/// <summary>
/// Writes the two editable sections of <c>board.config.json</c> back to disk: the
/// <c>iterations</c> table the sprint view edits (ABSD-401) and the
/// <c>assignees</c> map the assignee view edits (ABSD-402).
///
/// It edits the JSON document rather than re-serialising a <see cref="BoardConfig"/>.
/// That is the whole design: <see cref="BoardConfig"/> is a lossy view of the file
/// — it applies defaults, resolves relative paths to absolute ones and compiles
/// regexes — so writing one back would silently rewrite <c>board_file</c> as an
/// absolute path, bake this machine's defaults into a shared file, and drop any
/// key a newer CLI understands and this build does not.
///
/// The write is atomic (FSD NFR-7): a temp file in the same directory, renamed
/// over the original, so a crash mid-save cannot leave a half-written config that
/// neither the app nor the CLI can open.
/// </summary>
public static class BoardConfigWriter
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,

        // The config is hand-edited as often as it is written here, and the
        // default encoder escapes non-ASCII — a name with an accent in it would
        // come back as é and read as corruption to whoever opens the file next.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Replaces the <c>iterations</c> array, leaving every other key untouched.</summary>
    public static Result<bool> WriteIterations(
        string configPath, IReadOnlyList<IterationConfig> iterations)
    {
        var duplicate = iterations
            .GroupBy(i => i.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);

        if (duplicate is not null)
        {
            return Error.Validation(
                "config.duplicate_iteration",
                $"Two iterations are both named \"{duplicate.Key}\". Sprint names become iteration paths, so they must be unique.");
        }

        if (iterations.Any(i => string.IsNullOrWhiteSpace(i.Name)))
        {
            return Error.Validation(
                "config.unnamed_iteration",
                "An iteration with no name cannot become an iteration path. Name it or remove it.");
        }

        return Edit(configPath, root =>
        {
            var array = new JsonArray();
            foreach (var iteration in iterations)
            {
                var node = new JsonObject { ["name"] = iteration.Name.Trim() };

                // Absent rather than null: the schema allows the key to be missing,
                // and an explicit null is a different document that a stricter
                // reader may reject.
                if (!string.IsNullOrWhiteSpace(iteration.Start))
                {
                    node["start"] = iteration.Start!.Trim();
                }

                if (!string.IsNullOrWhiteSpace(iteration.Finish))
                {
                    node["finish"] = iteration.Finish!.Trim();
                }

                node["items"] = new JsonArray([
                    .. iteration.Items
                        .Select(code => code.Trim().ToUpperInvariant())
                        .Where(code => code.Length > 0)
                        .Distinct(StringComparer.Ordinal)
                        .Select(code => (JsonNode)JsonValue.Create(code)!)
                ]);

                array.Add(node);
            }

            root["iterations"] = array;
        });
    }

    /// <summary>Replaces the <c>assignees</c> map, leaving every other key untouched.</summary>
    public static Result<bool> WriteAssignees(
        string configPath, IReadOnlyDictionary<string, IReadOnlyList<string>> assignees)
    {
        if (assignees.Keys.Any(string.IsNullOrWhiteSpace))
        {
            return Error.Validation(
                "config.unnamed_assignee",
                "An assignee with no identity cannot be written to a work item. Name it or remove it.");
        }

        return Edit(configPath, root =>
        {
            var map = new JsonObject();

            // Ordered so two runs that changed nothing produce byte-identical
            // files: a config that reshuffles itself on every save turns every
            // review of it into a diff nobody can read.
            foreach (var (identity, codes) in assignees.OrderBy(a => a.Key, StringComparer.Ordinal))
            {
                map[identity.Trim()] = new JsonArray([
                    .. codes
                        .Select(code => code.Trim().ToUpperInvariant())
                        .Where(code => code.Length > 0)
                        .Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal)
                        .Select(code => (JsonNode)JsonValue.Create(code)!)
                ]);
            }

            root["assignees"] = map;
        });
    }

    /// <summary>
    /// Reads, edits and rewrites the document. The result is validated against the
    /// schema before it replaces the file: a config this app writes must be one it
    /// — and the CLI — can read back, and finding that out on the next open is too
    /// late to recover the old one.
    /// </summary>
    private static Result<bool> Edit(string configPath, Action<JsonObject> edit)
    {
        if (!File.Exists(configPath))
        {
            return Error.NotFound(
                "config.not_found",
                $"Config not found: {configPath}. A profile with no file on disk has nothing to write back to.");
        }

        string original;
        try
        {
            original = File.ReadAllText(configPath);
        }
        catch (IOException ex)
        {
            return Error.SourceFailure("config.unreadable", $"Could not read {configPath}: {ex.Message}");
        }

        JsonObject root;
        try
        {
            root = JsonNode.Parse(original) as JsonObject
                   ?? throw new JsonException("the document is not a JSON object");
        }
        catch (JsonException ex)
        {
            return Error.Validation(
                "config.invalid_json",
                $"{configPath} is not valid JSON, so it cannot be edited in place: {ex.Message}");
        }

        edit(root);

        var updated = root.ToJsonString(WriteOptions) + Environment.NewLine;

        if (BoardConfigSchema.Validate(updated) is { } violation)
        {
            return violation;
        }

        var temporary = $"{configPath}.tmp-{Guid.NewGuid():N}";
        try
        {
            File.WriteAllText(temporary, updated);
            File.Move(temporary, configPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
                // A leaked temp file is better than masking the write failure.
            }

            return Error.SourceFailure(
                "config.unsaved", $"Could not save {configPath}: {ex.Message}");
        }

        return true;
    }
}
