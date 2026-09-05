using System.Text.Json;
using System.Text.Json.Nodes;
using AdoBoardSync.Core.Configuration;

namespace AdoBoardSync.TestKit;

/// <summary>
/// A valid <see cref="BoardConfig" /> from a JSON document held in memory
/// (ABSD-107). <see cref="BoardConfig" />'s constructor is private and both
/// <c>BacklogParser</c> and <c>PatResolver</c> need an instance, so before this
/// existed every view-model test wrote a real config file through
/// <see cref="TempBoardProfile" /> merely to obtain one — which made a test that
/// asserts nothing about the filesystem depend on it anyway.
/// </summary>
public static class InMemoryConfig
{
    public const string DefaultBacklogPath = "/fixture/backlog.md";

    /// <summary>
    /// The fixture profile every in-memory test starts from: the same organisation,
    /// project and prefix <see cref="TempBoardProfile" /> writes, so a test can move
    /// between the two without its assertions changing.
    /// </summary>
    public static BoardConfig Create(
        string backlogPath = DefaultBacklogPath,
        string baseDirectory = "/fixture",
        Action<JsonObject>? customise = null)
    {
        var document = new JsonObject
        {
            ["org"] = "demo-org",
            ["project"] = "DemoProject",
            ["code_prefix"] = "PROJ",
            ["board_file"] = backlogPath,
        };

        customise?.Invoke(document);

        var parsed = BoardConfig.Parse(
            document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), baseDirectory);

        // A fixture that does not validate is a broken test, not a failing one, so
        // it fails loudly here rather than as a null reference three frames later.
        return parsed.IsSuccess
            ? parsed.Value
            : throw new InvalidOperationException(
                $"The in-memory fixture config is not valid: {parsed.Error!.Code} {parsed.Error.SafeMessage}");
    }
}
