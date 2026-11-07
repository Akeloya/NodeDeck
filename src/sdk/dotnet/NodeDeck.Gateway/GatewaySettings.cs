using System.Text.Json;
using System.Text.Json.Serialization;

namespace NodeDeck.Gateway;

[JsonConverter(typeof(JsonStringEnumConverter<GatewayMode>))]
public enum GatewayMode { Executor, Node }

public sealed record GatewaySettings
{
    public GatewayMode Mode { get; init; } = GatewayMode.Executor;
    public required string GatewayId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required string SharedSecret { get; init; }
    public GatewaySessionOptions Sessions { get; init; } = new();
    public JsonElement[] Listeners { get; init; } = [];
    public GatewayEndpointSettings[] Gateways { get; init; } = [];
    public TimeSpan CatalogRefreshInterval { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan RouteTimeout { get; init; } = TimeSpan.FromSeconds(15);

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(GatewayId);
        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(SharedSecret);
        ArgumentNullException.ThrowIfNull(Sessions);
        Sessions.Validate();
        if (!Enum.IsDefined(Mode) || GatewayId.Length > 128 || Listeners is null or { Length: 0 } || Gateways is null ||
            CatalogRefreshInterval <= TimeSpan.Zero || RouteTimeout <= TimeSpan.Zero || RouteTimeout > TimeSpan.FromMinutes(1))
            throw new ArgumentException("Invalid gateway mode, identity, listeners or routing intervals.");
        if (Mode == GatewayMode.Executor && Gateways.Length != 0)
            throw new ArgumentException("Only a node can configure downstream gateways.");
        var ids = new HashSet<string>(StringComparer.Ordinal) { GatewayId };
        foreach (var gateway in Gateways)
        {
            ArgumentNullException.ThrowIfNull(gateway);
            gateway.Validate();
            if (!ids.Add(gateway.GatewayId))
                throw new ArgumentException("Downstream gateway IDs must be unique and different from this gateway's ID.");
        }
    }
}

/// <summary>A directly configured peer. Metadata is refreshed from the authenticated peer while connected.</summary>
public sealed record GatewayEndpointSettings
{
    public required string GatewayId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required string Host { get; init; }
    public int Port { get; init; } = 5050;
    public required string SharedSecret { get; init; }

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(GatewayId);
        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(Host);
        ArgumentException.ThrowIfNullOrWhiteSpace(SharedSecret);
        if (GatewayId.Length > 128 || Port is < 1 or > 65535)
            throw new ArgumentException("Invalid downstream gateway identity or TCP port.");
    }
}
