namespace NodeDeck.Core.Protocol;

/// <summary>Shared discovery contract configured identically on the phone and gateway.</summary>
public sealed record BleServiceIdentifiers
{
    public Guid ServiceUuid { get; init; } = Guid.Parse(BleProtocolConstants.ServiceUuid);
    public Guid ClientToGatewayCharacteristicUuid { get; init; } = Guid.Parse(BleProtocolConstants.ClientToGatewayCharacteristicUuid);
    public Guid GatewayToClientCharacteristicUuid { get; init; } = Guid.Parse(BleProtocolConstants.GatewayToClientCharacteristicUuid);

    public void Validate()
    {
        Guid[] identifiers = [ServiceUuid, ClientToGatewayCharacteristicUuid, GatewayToClientCharacteristicUuid];
        if (identifiers.Contains(Guid.Empty) || identifiers.Distinct().Count() != identifiers.Length)
            throw new ArgumentException("Configure three distinct, non-empty BLE service and characteristic UUIDs.");
    }
}
