using System.Text.Json;
using System.Text.Json.Nodes;
using AdoBoardSync.Core.Backlog;
using AdoBoardSync.Core.Configuration;
using AdoBoardSync.Core.Results;

namespace AdoBoardSync.Desktop.Services;

/// <summary>
/// A Board profile being filled in by hand rather than loaded from a file. It
/// composes the same JSON <c>board.config.json</c> holds and validates it through
/// <see cref="BoardConfig.Parse"/>, so the result is byte-compatible with one
/// written by hand.
/// </summary>
public sealed class BoardProfileDraft
{
    public string Organisation { get; set; } = string.Empty;

    public string Project { get; set; } = string.Empty;

    public string CodePrefix { get; set; } = string.Empty;

    public string BacklogPath { get; set; } = string.Empty;

    public string Team { get; set; } = string.Empty;

    /// <summary>Validates the entered values and produces a usable config.</summary>
    /// <param name="store">
    ///     The file seam. The draft asks the store whether the backlog is there
    ///     rather than probing the filesystem itself, so onboarding is testable
    ///     with no disk (ABSD-107).
    /// </param>
    public Result<BoardConfig> Build(IBacklogFileStore store)
    {
        if (string.IsNullOrWhiteSpace(Organisation))
        {
            return Error.Validation("profile.org_required", "Enter the Azure DevOps organisation.");
        }

        if (string.IsNullOrWhiteSpace(Project))
        {
            return Error.Validation("profile.project_required", "Enter the project name.");
        }

        if (string.IsNullOrWhiteSpace(CodePrefix))
        {
            return Error.Validation(
                "profile.prefix_required",
                "Enter the issue code prefix — the letters before the hyphen in an issue code.");
        }

        if (string.IsNullOrWhiteSpace(BacklogPath))
        {
            return Error.Validation("profile.backlog_required", "Choose the backlog Markdown file.");
        }

        if (!store.Exists(BacklogPath))
        {
            return Error.NotFound("profile.backlog_missing", $"Backlog file not found: {BacklogPath}");
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(BacklogPath))
            ?? Directory.GetCurrentDirectory();

        return BoardConfig.Parse(ToJson(), directory);
    }

    /// <summary>
    /// Writes the profile as a <c>board.config.json</c> — exactly the document
    /// <see cref="Build"/> validated. Optional; the app runs from memory too.
    /// </summary>
    public Result<string> SaveTo(IBacklogFileStore store, string configPath)
    {
        var built = Build(store);
        if (built.IsFailure)
        {
            return built.Error!;
        }

        var written = store.WriteAtomic(configPath, ToJson(), IBacklogFileStore.ConfigScope);
        return written.IsFailure ? written.Error! : configPath;
    }

    private string ToJson()
    {
        var config = new JsonObject
        {
            ["org"] = Organisation.Trim(),
            ["project"] = Project.Trim(),
            ["code_prefix"] = CodePrefix.Trim().ToUpperInvariant(),
            ["board_file"] = Path.GetFullPath(BacklogPath.Trim()),
        };

        // An empty string is not a team name; omit the key rather than write one
        // the CLI would try to look up.
        if (!string.IsNullOrWhiteSpace(Team))
        {
            config["team"] = Team.Trim();
        }

        return config.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
