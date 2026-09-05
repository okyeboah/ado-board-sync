using AdoBoardSync.Core.Board;

namespace AdoBoardSync.Infrastructure;

/// <summary>The real connector, built around whichever token resolved this time.</summary>
public sealed class AzureDevOpsGatewayFactory : IBoardGatewayFactory
{
    public IBoardGateway Create(string personalAccessToken) =>
        new AzureDevOpsGateway(personalAccessToken);
}
