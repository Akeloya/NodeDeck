using NodeDeck.Core.Protocol;

namespace NodeDeck.Gateway;

internal abstract class GatewayRoutingSession : IAsyncDisposable
{
    public abstract Task<ProtocolResponse> HandleAsync(ProtocolRequest request, CancellationToken cancellationToken);
    public abstract void Revoke();
    public abstract ValueTask DisposeAsync();
}
