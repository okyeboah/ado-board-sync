using AdoBoardSync.Core.Configuration;
using AdoBoardSync.Core.Results;

namespace AdoBoardSync.Core.Board;

/// <summary>
/// Builds a connector for one resolved token. The gateway cannot be a singleton in
/// the composition root because it is constructed around a credential, and the
/// credential is resolved per profile and never cached (ARCHITECTURE.md §6) — so
/// the container holds this delegate, and the token reaches a gateway only at the
/// moment a board action runs.
///
/// Named rather than a bare <c>Func&lt;string, IBoardGateway&gt;</c> so the parameter
/// keeps its meaning at every call site, and so a second string-keyed gateway
/// factory cannot collide with this one in the container.
/// </summary>
/// <param name="personalAccessToken">
/// The resolved PAT. The caller owns the result and disposes it; nothing retains
/// the token.
/// </param>
public delegate IBoardGateway BoardGatewayFactory(string personalAccessToken);

/// <summary>
/// The gateway a caller gets when no factory was supplied — the board equivalent of
/// <see cref="UnavailableCredentialStore" />.
///
/// Every method fails with <c>board.unconfigured</c> rather than throwing, because
/// a missing registration is an expected outcome at this seam and ARCHITECTURE.md
/// §3.7 keeps exceptions out of expected outcomes. The failure lands in the
/// surface's error text through the branch every other board failure takes.
/// </summary>
public sealed class UnconfiguredBoardGateway(string reason) : IBoardGateway
{
    public Task<Result<BoardSnapshot>> ReadAsync(
        BoardConfig config, CancellationToken cancellationToken = default) => Fail<BoardSnapshot>();

    public Task<Result<int>> CreateAsync(
        BoardConfig config, string workItemType, string title, string descriptionHtml,
        int? parentId, CancellationToken cancellationToken = default) => Fail<int>();

    public Task<Result<bool>> UpdateAsync(
        BoardConfig config, int workItemId, IReadOnlyList<BoardFieldChange> changes,
        CancellationToken cancellationToken = default) => Fail<bool>();

    public Task<Result<bool>> DeleteAsync(
        BoardConfig config, int workItemId, CancellationToken cancellationToken = default) => Fail<bool>();

    public Task<Result<IterationNode>> EnsureIterationAsync(
        BoardConfig config, string name, string? start, string? finish,
        CancellationToken cancellationToken = default) => Fail<IterationNode>();

    public Task<Result<string?>> DefaultTeamAsync(
        BoardConfig config, CancellationToken cancellationToken = default) => Fail<string?>();

    public Task<Result<bool>> AddTeamIterationAsync(
        BoardConfig config, string team, string identifier,
        CancellationToken cancellationToken = default) => Fail<bool>();

    private Task<Result<T>> Fail<T>() => Task.FromResult<Result<T>>(
        Error.SourceFailure(
            "board.unconfigured",
            $"No board connector is configured: {reason}. Resolve this surface from AppServices."));
}
