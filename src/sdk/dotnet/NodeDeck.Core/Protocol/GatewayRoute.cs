namespace NodeDeck.Core.Protocol;

/// <summary>Forwarding history. Nodes append themselves and never forward to an already visited peer.</summary>
public sealed record GatewayRoute(IReadOnlyList<string> VisitedGatewayIds, int RemainingHops)
{
    public const int MaximumHops = 16;
    public static GatewayRoute Empty => new([], MaximumHops);

    public void ValidateAt(string gatewayId)
    {
        if (VisitedGatewayIds is null || RemainingHops < 0 || RemainingHops > MaximumHops ||
            VisitedGatewayIds.Count > MaximumHops - RemainingHops ||
            VisitedGatewayIds.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 128))
            throw new InvalidRequestException("Invalid gateway route.");
        if (VisitedGatewayIds.Distinct(StringComparer.Ordinal).Count() != VisitedGatewayIds.Count ||
            VisitedGatewayIds.Contains(gatewayId, StringComparer.Ordinal))
            throw new GatewayRoutingException(ProtocolErrorCode.RouteLoop, "The request cannot return to a gateway already on its route.");
    }

    public GatewayRoute Forward(string gatewayId, string destinationId)
    {
        ValidateAt(gatewayId);
        if (destinationId == gatewayId || VisitedGatewayIds.Contains(destinationId, StringComparer.Ordinal))
            throw new GatewayRoutingException(ProtocolErrorCode.RouteLoop, "The request cannot be routed backwards.");
        if (RemainingHops == 0)
            throw new GatewayRoutingException(ProtocolErrorCode.RouteHopLimit, "The gateway route hop limit was reached.");
        return new GatewayRoute([.. VisitedGatewayIds, gatewayId], RemainingHops - 1);
    }
}

public sealed class GatewayRoutingException(string code, string message)
    : ProtocolValidationException(code, message);
