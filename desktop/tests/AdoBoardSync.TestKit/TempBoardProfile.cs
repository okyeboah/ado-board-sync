using System.Text.Json;
using System.Text.Json.Nodes;

namespace AdoBoardSync.TestKit;

/// <summary>
/// A throwaway <c>board.config.json</c> pointing at a chosen backlog file.
///
/// Each instance owns a private temp directory. The CLI resolves a relative
/// <c>pat_file</c> against the config's directory, so two tests sharing one
/// directory would see each other's token file; a per-test directory keeps that
/// impossible.
/// </summary>
public sealed class TempBoardProfile : IDisposable
{
    private TempBoardProfile(string directory, string configPath)
    {
        Directory = directory;
        ConfigPath = configPath;
    }

    public string Directory { get; }

    public string ConfigPath { get; }

    public static TempBoardProfile Create(string backlogPath, Action<JsonObject>? customise = null)
    {
        var directory = System.IO.Directory.CreateTempSubdirectory("abs-parity-").FullName;

        var config = new JsonObject
        {
            ["org"] = "demo-org",
            ["project"] = "DemoProject",
            ["code_prefix"] = "PROJ",
            ["board_file"] = backlogPath,
            ["stop_headings"] = new JsonArray("## Appendix")
        };

        customise?.Invoke(config);

        var configPath = Path.Combine(directory, "board.config.json");
        File.WriteAllText(configPath, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return new TempBoardProfile(directory, configPath);
    }

    public void Dispose()
    {
        if (System.IO.Directory.Exists(Directory))
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}
