using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AdoBoardSync.Core.Results;

namespace AdoBoardSync.Core.Configuration;

public sealed record IterationConfig(
    string Name,
    string? Start,
    string? Finish,
    IReadOnlyList<string> Items);

/// <summary>
/// Per-project configuration loaded from <c>board.config.json</c>.
///
/// A port of the CLI's <c>config.py</c>, including its defaults, so the desktop
/// app and the CLI read the same file the same way. The PAT is never stored
/// here: <see cref="ResolvePat"/> reads it from the environment or a gitignored
/// token file at call time, and it is never logged, cached, or written back.
/// </summary>
public sealed class BoardConfig
{
    public const string DefaultApiVersion = "7.1";
    public const string DefaultBoardFile = "docs/backlog.md";
    public const string DefaultCsvFile = "build/work-items.csv";
    public const string DefaultEpicHeadingRegex = @"^##\s+(Epic\b.*)$";
    public const string DefaultPatEnv = "AZURE_DEVOPS_PAT";
    public const string DefaultPatFile = ".ado_pat";
    public const int DefaultTaskTitleMax = 250;
    public const int DefaultMaxRetries = 3;
    public const double DefaultBackoff = 1.5;
    public const int DefaultTimeout = 20;

    private BoardConfig(BoardConfigData data, string baseDirectory)
    {
        BaseDirectory = baseDirectory;
        Org = data.Org!;
        Project = data.Project!;
        CodePrefix = data.CodePrefix!;
        ApiVersion = data.ApiVersion ?? DefaultApiVersion;
        BoardFile = ToAbsolute(data.BoardFile ?? DefaultBoardFile, baseDirectory);
        CsvFile = ToAbsolute(data.CsvFile ?? DefaultCsvFile, baseDirectory);

        Types = Merge(
            new Dictionary<string, string> { ["epic"] = "Epic", ["story"] = "Issue", ["task"] = "Task" },
            data.Types);

        // Workflow state names. `done` is the terminal state used by close-children
        // to cascade an Issue's completion onto its child Tasks. Basic uses "Done";
        // Agile/CMMI use "Closed"/"Resolved".
        States = Merge(new Dictionary<string, string> { ["done"] = "Done" }, data.States);

        EpicHeadingRegex = new Regex(data.EpicHeadingRegex ?? DefaultEpicHeadingRegex);
        StopHeadings = data.StopHeadings ?? [];
        PatEnv = data.PatEnv ?? DefaultPatEnv;
        PatFile = data.PatFile ?? DefaultPatFile;
        TaskTitleMax = data.TaskTitleMax ?? DefaultTaskTitleMax;
        MaxRetries = data.MaxRetries ?? DefaultMaxRetries;
        Backoff = data.Backoff ?? DefaultBackoff;
        Timeout = data.Timeout ?? DefaultTimeout;
        Team = data.Team;
        Iterations = data.Iterations ?? [];
        Assignees = data.Assignees ?? new Dictionary<string, IReadOnlyList<string>>();

        IssueCodeRegex = new Regex($"({Regex.Escape(CodePrefix)}-\\d+)");
        BaseUrl = $"https://dev.azure.com/{Org}/{Project}/_apis";
        OrgUrl = $"https://dev.azure.com/{Org}/_apis";
    }

    public string BaseDirectory { get; }
    public string Org { get; }
    public string Project { get; }
    public string CodePrefix { get; }
    public string ApiVersion { get; }
    public string BoardFile { get; }
    public string CsvFile { get; }
    public IReadOnlyDictionary<string, string> Types { get; }
    public IReadOnlyDictionary<string, string> States { get; }
    public Regex EpicHeadingRegex { get; }
    public Regex IssueCodeRegex { get; }
    public IReadOnlyList<string> StopHeadings { get; }
    public string PatEnv { get; }
    public string PatFile { get; }
    public int TaskTitleMax { get; }
    public int MaxRetries { get; }
    public double Backoff { get; }
    public int Timeout { get; }
    public string? Team { get; }
    public IReadOnlyList<IterationConfig> Iterations { get; }
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Assignees { get; }
    public string BaseUrl { get; }
    public string OrgUrl { get; }

    public static Result<BoardConfig> Load(string configPath)
    {
        if (!File.Exists(configPath))
        {
            return Error.NotFound(
                "config.not_found",
                $"Config not found: {configPath}. Create one from board.config.example.json.");
        }

        string json;
        try
        {
            json = File.ReadAllText(configPath);
        }
        catch (IOException ex)
        {
            return Error.SourceFailure("config.unreadable", $"Could not read {configPath}: {ex.Message}");
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(configPath));
        return Parse(json, directory ?? Directory.GetCurrentDirectory());
    }

    public static Result<BoardConfig> Parse(string json, string baseDirectory)
    {
        // Schema first, on the raw document: a mistyped key does not survive
        // deserialization, so by the time there is a typed object the evidence is gone.
        if (BoardConfigSchema.Validate(json) is { } violation)
        {
            return violation;
        }

        BoardConfigData? data;
        try
        {
            data = JsonSerializer.Deserialize(json, BoardConfigJsonContext.Default.BoardConfigData);
        }
        catch (JsonException ex)
        {
            return Error.Validation("config.invalid_json", $"board.config.json is not valid JSON: {ex.Message}");
        }

        if (data is null)
        {
            return Error.Validation("config.empty", "board.config.json is empty.");
        }

        // Keys and values are paired so the two cannot fall out of step.
        var missing = new[]
        {
            ("org", data.Org),
            ("project", data.Project),
            ("code_prefix", data.CodePrefix)
        }
            .Where(required => string.IsNullOrEmpty(required.Item2))
            .Select(required => required.Item1)
            .ToList();

        if (missing.Count > 0)
        {
            return Error.Validation(
                "config.missing_keys",
                $"board.config.json must set: {string.Join(", ", missing)}.");
        }

        try
        {
            return new BoardConfig(data, baseDirectory);
        }
        catch (ArgumentException ex)
        {
            return Error.Validation("config.bad_regex", $"epic_heading_regex is not a valid regex: {ex.Message}");
        }
    }

    /// <summary>
    /// Resolves a path from the config against the config file's directory, the way
    /// the CLI does. Credential resolution lives in <see cref="PatResolver"/>, which
    /// uses this for the token file.
    /// </summary>
    public string ResolvePath(string path) => ToAbsolute(path, BaseDirectory);

    private static string ToAbsolute(string path, string baseDirectory) =>
        Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(baseDirectory, path));

    private static Dictionary<string, string> Merge(
        Dictionary<string, string> defaults,
        IReadOnlyDictionary<string, string>? overrides)
    {
        if (overrides is null)
        {
            return defaults;
        }

        foreach (var (key, value) in overrides)
        {
            defaults[key] = value;
        }

        return defaults;
    }
}

public sealed class BoardConfigData
{
    [JsonPropertyName("org")] public string? Org { get; init; }
    [JsonPropertyName("project")] public string? Project { get; init; }
    [JsonPropertyName("code_prefix")] public string? CodePrefix { get; init; }
    [JsonPropertyName("api_version")] public string? ApiVersion { get; init; }
    [JsonPropertyName("board_file")] public string? BoardFile { get; init; }
    [JsonPropertyName("csv_file")] public string? CsvFile { get; init; }
    [JsonPropertyName("types")] public Dictionary<string, string>? Types { get; init; }
    [JsonPropertyName("states")] public Dictionary<string, string>? States { get; init; }
    [JsonPropertyName("epic_heading_regex")] public string? EpicHeadingRegex { get; init; }
    [JsonPropertyName("stop_headings")] public List<string>? StopHeadings { get; init; }
    [JsonPropertyName("pat_env")] public string? PatEnv { get; init; }
    [JsonPropertyName("pat_file")] public string? PatFile { get; init; }
    [JsonPropertyName("task_title_max")] public int? TaskTitleMax { get; init; }
    [JsonPropertyName("max_retries")] public int? MaxRetries { get; init; }
    [JsonPropertyName("backoff")] public double? Backoff { get; init; }
    [JsonPropertyName("timeout")] public int? Timeout { get; init; }
    [JsonPropertyName("team")] public string? Team { get; init; }
    [JsonPropertyName("iterations")] public List<IterationConfig>? Iterations { get; init; }
    [JsonPropertyName("assignees")] public Dictionary<string, IReadOnlyList<string>>? Assignees { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(BoardConfigData))]
internal sealed partial class BoardConfigJsonContext : JsonSerializerContext;
