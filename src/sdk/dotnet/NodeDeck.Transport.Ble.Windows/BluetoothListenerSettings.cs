using NodeDeck.Core.Protocol;
using NodeDeck.Core.Settings;
using NLog;
using Windows.Devices.Bluetooth.Advertisement;
using System.Text.Json.Serialization;

namespace NodeDeck.Transport.Ble.Windows;

public sealed class BluetoothListenerSettings : IListenerSettings
{
    public string Type => "bluetooth";
    public LogLevel LogLevel { get; init; } = LogLevel.Info;
    public Guid ServiceUuid { get; init; } = Guid.Parse(BleProtocolConstants.ServiceUuid);
    public Guid ClientToGatewayCharacteristicUuid { get; init; } = Guid.Parse(BleProtocolConstants.ClientToGatewayCharacteristicUuid);
    public Guid GatewayToClientCharacteristicUuid { get; init; } = Guid.Parse(BleProtocolConstants.GatewayToClientCharacteristicUuid);
    [JsonConverter(typeof(JsonStringEnumConverter<BluetoothLEScanningMode>))]
    public BluetoothLEScanningMode ScanningMode { get; init; } = BluetoothLEScanningMode.Active;
    public int ChunkSize { get; init; } = BleProtocolConstants.ChunkSize;
    public int InitialMaxMessageBytes { get; init; } = ProtocolConstants.InitialMaxMessageBytes;
}
