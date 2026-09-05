using AdoBoardSync.Core.Results;

namespace AdoBoardSync.Core.Configuration;

/// <summary>
/// A PAT typed into the app and held for this session only, so a first-time user
/// can reach a board without setting an environment variable or writing a token
/// file. The value lives in this object and nowhere else: nothing here writes to
/// disk, and <see cref="Name"/> never reveals the token.
/// </summary>
public sealed class SessionPatSource(string token) : IPatSource
{
    public string Name => "the token entered in this session";

    public Result<string?> TryRead() =>
        string.IsNullOrWhiteSpace(token) ? (string?)null : token.Trim();
}
