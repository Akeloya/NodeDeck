using NodeDeck.Core.Protocol;

namespace NodeDeck.Gateway;

/// <summary>Common host contract for a command executor and a routing node.</summary>
public interface IGateway : IAsyncDisposable
{
    GatewayInfo Info { get; }
    Task Ready { get; }
    IReadOnlyList<GatewaySessionInfo> ActiveSessions { get; }
    IReadOnlyList<GatewayConnectionInfo> ConnectedGateways { get; }
    void UpdateMetadata(string name, string? description);
    bool EndSession(string sessionId);
    void Start();
    Task RunAsync(CancellationToken cancellationToken = default);
}

public sealed record GatewayConnectionInfo(GatewayInfo Gateway, bool IsConnected, DateTimeOffset? LastSeenAt);
