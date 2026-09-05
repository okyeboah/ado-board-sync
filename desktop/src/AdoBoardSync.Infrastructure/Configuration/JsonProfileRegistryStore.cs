using System.Text.Json;
using System.Text.Json.Serialization;
using AdoBoardSync.Core.Configuration;
using AdoBoardSync.Core.Results;

namespace AdoBoardSync.Infrastructure.Configuration;

/// <summary>
/// The profile registry as a JSON file under the user's own local application data
/// (ABSD-502).
///
/// Not in the repository, and not beside a backlog: a backlog lives in a git
/// checkout, and a file that follows it there is a file that eventually gets
/// committed — this one names every board a person works on. It carries no token
/// either, by construction: <see cref="ProfileEntry"/> has nowhere to put one.
///
/// Written temp-then-rename like <see cref="BoardConfigWriter"/>, so a crash
/// mid-write leaves the previous registry intact rather than a truncated document
/// that loses every profile the user had added.
/// </summary>
public sealed class JsonProfileRegistryStore : IProfileRegistryStore
{
    /// <param name="path">Where the file lives. Defaults to the user's own local
    /// application data; tests pass a temporary path.</param>
    public JsonProfileRegistryStore(string? path = null)
    {
        RegistryPath = path ?? DefaultPath();
    }

    public string RegistryPath { get; }

    public static string DefaultPath()
    {
        return Path.Combine(LocalDataPaths.Directory("AdoBoardSync"), "profiles.json");
    }

    public Result<ProfileRegistry> Read()
    {
        if (!File.Exists(RegistryPath))
        {
            return ProfileRegistry.Empty;
        }

        string json;
        try
        {
            json = File.ReadAllText(RegistryPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Error.SourceFailure(
                "profiles.unreadable", $"Could not read the profile registry at {RegistryPath}: {ex.Message}");
        }

        ProfileRegistryDocument? document;
        try
        {
            document = JsonSerializer.Deserialize(json, ProfileRegistryJsonContext.Default.ProfileRegistryDocument);
        }
        catch (JsonException ex)
        {
            return Error.Validation(
                "profiles.invalid_json",
                $"The profile registry at {RegistryPath} is not valid JSON: {ex.Message}. "
                + "Fix or delete the file — rewriting it here would discard the profiles it still names.");
        }

        if (document is null)
        {
            return ProfileRegistry.Empty;
        }

        // Folded back through Add rather than assigned, so a hand-edited file obeys
        // the same rules as the running app: one entry per path, and an active
        // profile that is actually in the list.
        var registry = ProfileRegistry.Empty;
        foreach (var entry in document.Profiles ?? [])
        {
            var added = registry.Add(new ProfileEntry(
                entry.ConfigPath ?? string.Empty,
                entry.Org ?? string.Empty,
                entry.Project ?? string.Empty,
                entry.DisplayName ?? string.Empty));

            if (added.IsFailure)
            {
                return Error.Validation(
                    "profiles.invalid_entry",
                    $"The profile registry at {RegistryPath} holds an entry that cannot be used: "
                    + $"{added.Error!.SafeMessage}");
            }

            registry = added.Value;
        }

        if (document.ActiveConfigPath is { Length: > 0 } active
            && registry.SetActive(active) is { IsSuccess: true } activated)
        {
            registry = activated.Value;
        }

        // A stored active naming no known profile keeps the first entry that Add
        // chose. Repairing beats refusing: a registry that will not load is a
        // switcher that will not open, and the file is not the user's to fix.
        return registry;
    }

    public Result<bool> Write(ProfileRegistry registry)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(RegistryPath));

        var document = new ProfileRegistryDocument
        {
            Version = 1,
            ActiveConfigPath = registry.ActiveConfigPath,
            Profiles =
            [
                .. registry.Profiles.Select(p => new ProfileEntryDocument
                {
                    ConfigPath = p.ConfigPath,
                    Org = p.Org,
                    Project = p.Project,
                    DisplayName = p.DisplayName,
                })
            ],
        };

        var json = JsonSerializer.Serialize(
            document, ProfileRegistryJsonContext.Default.ProfileRegistryDocument) + Environment.NewLine;

        var temporary = $"{RegistryPath}.tmp-{Guid.NewGuid():N}";
        try
        {
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(temporary, json);
            File.Move(temporary, RegistryPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
            }

            return Error.SourceFailure(
                "profiles.unsaved", $"Could not save the profile registry to {RegistryPath}: {ex.Message}");
        }

        return true;
    }
}

public sealed class ProfileEntryDocument
{
    [JsonPropertyName("config_path")] public string? ConfigPath { get; init; }

    [JsonPropertyName("org")] public string? Org { get; init; }

    [JsonPropertyName("project")] public string? Project { get; init; }

    [JsonPropertyName("display_name")] public string? DisplayName { get; init; }
}

public sealed class ProfileRegistryDocument
{
    [JsonPropertyName("version")] public int Version { get; init; }

    [JsonPropertyName("active_config_path")] public string? ActiveConfigPath { get; init; }

    [JsonPropertyName("profiles")] public List<ProfileEntryDocument>? Profiles { get; init; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ProfileRegistryDocument))]
internal sealed partial class ProfileRegistryJsonContext : JsonSerializerContext;
