namespace AdoBoardSync.Core.Board;

/// <summary>
/// Builds a connector for one resolved token. The gateway cannot be a singleton in
/// the composition root because it is constructed around a credential, and the
/// credential is resolved per profile and never cached (ARCHITECTURE.md §6) — so
/// the container holds this factory, and the token reaches a gateway only at the
/// moment a board action runs.
/// </summary>
public interface IBoardGatewayFactory
{
    /// <summary>
    /// A connector authenticated with <paramref name="personalAccessToken" />. The
    /// caller owns the result and disposes it; nothing here retains the token.
    /// </summary>
    IBoardGateway Create(string personalAccessToken);
}
