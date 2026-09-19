using AdoBoardSync.Core.Agents;
using AdoBoardSync.Core.Results;

namespace AdoBoardSync.Infrastructure.Agents;

/// <summary>
/// The one adapter that reads and writes the backlog as bytes.
///
/// The port it implements is declared in Core, so the seam a test drives is the
/// same one the app runs on.
/// </summary>
public sealed class FileSystemAgentEditFileStore : IAgentEditFileStore
{
    public Result<byte[]> ReadBytes(string path)
    {
        if (!File.Exists(path))
        {
            return Error.NotFound("agent.edit.not_found", $"File not found: {path}.");
        }

        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Error.SourceFailure("agent.edit.unreadable", $"Could not read {path}: {ex.Message}");
        }
    }

    public Result<bool> WriteBytes(string path, byte[] bytes)
    {
        try
        {
            File.WriteAllBytes(path, bytes);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Error.SourceFailure(
                "agent.edit.unwritable",
                $"Could not put {path} back as it was: {ex.Message}");
        }
    }
}
