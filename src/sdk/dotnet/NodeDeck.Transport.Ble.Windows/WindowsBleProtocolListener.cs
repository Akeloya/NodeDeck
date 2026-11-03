using System.Threading.Channels;
using NodeDeck.Protocol;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace NodeDeck.Transport.Ble.Windows;

/// <summary>Windows BLE central that connects to the NodeDeck GATT peripheral advertised by the phone.</summary>
public sealed class WindowsBleProtocolListener : IProtocolMessageListener
{
    public static readonly Guid ServiceUuid = Guid.Parse(BleProtocolConstants.ServiceUuid);
    public static readonly Guid ClientToGatewayCharacteristicUuid = Guid.Parse(BleProtocolConstants.ClientToGatewayCharacteristicUuid);
    public static readonly Guid GatewayToClientCharacteristicUuid = Guid.Parse(BleProtocolConstants.GatewayToClientCharacteristicUuid);

    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<IProtocolMessageConnection> _connections = Channel.CreateUnbounded<IProtocolMessageConnection>();
    private BluetoothLEAdvertisementWatcher? _watcher;
    private WindowsBleProtocolConnection? _connection;
    private int _connecting;
    private bool _started;

    public event Action<string>? Diagnostic;

    public Task Ready => _ready.Task;

    public void Start()
    {
        if (_started) throw new InvalidOperationException("BLE listener has already started.");
        _started = true;
        // Android implementations may put a 128-bit service UUID in the scan response.
        // A strict OS advertisement filter only matches the primary advertisement and
        // can therefore silently miss the phone. Filter the parsed advertisement here.
        _watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        _watcher.Stopped += (_, args) => Diagnostic?.Invoke($"BLE scanner stopped: {args.Error}; watcher status is {_watcher.Status}.");
        _watcher.Received += OnAdvertisementReceived;
        _watcher.Start();
        Diagnostic?.Invoke($"BLE advertisement watcher status: {_watcher.Status} (active scan, no OS-side UUID filter).");
    }

    private void OnAdvertisementReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        var advertised = string.Join(",", args.Advertisement.ServiceUuids);
        Diagnostic?.Invoke($"BLE advertisement from {args.BluetoothAddress:X12}; RSSI {args.RawSignalStrengthInDBm}; service UUIDs: {advertised}; name: '{args.Advertisement.LocalName}'.");
        if (!args.Advertisement.ServiceUuids.Contains(ServiceUuid)) return;
        Diagnostic?.Invoke("Found NodeDeck service in advertisement; connecting and discovering GATT service.");
        if (Interlocked.CompareExchange(ref _connecting, 1, 0) == 0)
            _ = ConnectAsync(args.BluetoothAddress);
    }

    private async Task ConnectAsync(ulong address)
    {
        try
        {
            var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address)
                ?? throw new IOException("Could not open the advertising phone over BLE.");
            var services = await device.GetGattServicesForUuidAsync(ServiceUuid, BluetoothCacheMode.Uncached);
            Diagnostic?.Invoke($"GATT service discovery result: {services.Status}; matches: {services.Services.Count}.");
            if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
                throw new IOException($"NodeDeck BLE service discovery failed: {services.Status}.");
            var service = services.Services[0];

            var txResult = await service.GetCharacteristicsForUuidAsync(ClientToGatewayCharacteristicUuid, BluetoothCacheMode.Uncached);
            var rxResult = await service.GetCharacteristicsForUuidAsync(GatewayToClientCharacteristicUuid, BluetoothCacheMode.Uncached);
            if (txResult.Status != GattCommunicationStatus.Success || txResult.Characteristics.Count == 0 ||
                rxResult.Status != GattCommunicationStatus.Success || rxResult.Characteristics.Count == 0)
                throw new IOException("The phone is missing a NodeDeck BLE characteristic.");

            var tx = txResult.Characteristics[0];
            var rx = rxResult.Characteristics[0];
            var connection = new WindowsBleProtocolConnection(device, rx, tx);
            tx.ValueChanged += connection.OnValueChanged;
            try
            {
                var cccd = await tx.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify);
                Diagnostic?.Invoke($"GATT notification subscription result: {cccd}.");
                if (cccd != GattCommunicationStatus.Success) throw new IOException($"Could not subscribe to phone BLE notifications: {cccd}.");
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
            connection.MonitorDisconnection(message => Diagnostic?.Invoke(message));
            _connection = connection;
            _watcher?.Stop();
            _connections.Writer.TryWrite(_connection);
            _ready.TrySetResult();
            Diagnostic?.Invoke("BLE GATT connection is ready.");
        }
        catch (Exception ex)
        {
            Diagnostic?.Invoke($"BLE connection attempt failed: {ex.GetType().Name}: {ex.Message}");
            Interlocked.Exchange(ref _connecting, 0);
            _ready.TrySetException(new IOException("Could not connect to the phone's NodeDeck BLE service. Check that Bluetooth LE mode is active on the phone and that the computer supports BLE central mode.", ex));
        }
    }

    public async Task<IProtocolMessageConnection> AcceptAsync(CancellationToken cancellationToken = default)
    {
        if (!_started) throw new InvalidOperationException("Start the BLE listener before accepting a connection.");
        return await _connections.Reader.ReadAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _watcher?.Stop();
        if (_connection is not null) await _connection.DisposeAsync();
        _connections.Writer.TryComplete();
    }

    private sealed class WindowsBleProtocolConnection : IProtocolMessageConnection
    {
        private readonly BluetoothLEDevice _device;
        private readonly GattCharacteristic _rx;
        private readonly GattCharacteristic _tx;
        private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        private readonly MemoryStream _frame = new();
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly object _frameLock = new();
        private int _maxMessageBytes = ProtocolConstants.InitialMaxMessageBytes;

        public WindowsBleProtocolConnection(BluetoothLEDevice device, GattCharacteristic rx, GattCharacteristic tx)
        {
            _device = device;
            _rx = rx;
            _tx = tx;
        }

        private Action<string>? _diagnostic;
        private int _disposed;

        public void MonitorDisconnection(Action<string> diagnostic)
        {
            _diagnostic = diagnostic;
            _device.ConnectionStatusChanged += OnConnectionStatusChanged;
            OnConnectionStatusChanged(_device, null);
        }

        private void OnConnectionStatusChanged(BluetoothLEDevice device, object? args)
        {
            if (device.ConnectionStatus == BluetoothConnectionStatus.Disconnected && _incoming.Writer.TryComplete())
                _diagnostic?.Invoke("Bluetooth LE connection to phone lost.");
        }

        public int MaxMessageBytes => _maxMessageBytes;
        public void SetMaxMessageBytes(int maxMessageBytes)
        {
            if (maxMessageBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxMessageBytes));
            _maxMessageBytes = maxMessageBytes;
        }

        public void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            var reader = DataReader.FromBuffer(args.CharacteristicValue);
            var bytes = new byte[args.CharacteristicValue.Length];
            reader.ReadBytes(bytes);
            lock (_frameLock)
            {
                foreach (var value in bytes)
                {
                    if (value == (byte)'\n')
                    {
                        if (_frame.Length == 0) continue;
                        var frame = _frame.ToArray();
                        _frame.SetLength(0);
                        if (frame[^1] == (byte)'\r') Array.Resize(ref frame, frame.Length - 1);
                        if (frame.Length > _maxMessageBytes)
                        {
                            _incoming.Writer.TryComplete(new ProtocolValidationException("message_too_large", "BLE message exceeds the negotiated limit."));
                            return;
                        }
                        _incoming.Writer.TryWrite(frame);
                    }
                    else
                    {
                        if (_frame.Length >= _maxMessageBytes)
                        {
                            _incoming.Writer.TryComplete(new ProtocolValidationException("message_too_large", "BLE message exceeds the negotiated limit."));
                            return;
                        }
                        _frame.WriteByte(value);
                    }
                }
            }
        }

        public async Task SendAsync<T>(T message, CancellationToken cancellationToken = default)
        {
            var frame = ProtocolJson.Serialize(message).Append((byte)'\n').ToArray();
            if (frame.Length - 1 > _maxMessageBytes) throw new ProtocolValidationException("message_too_large", "BLE message exceeds the negotiated limit.");
            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                for (var offset = 0; offset < frame.Length; offset += 20)
                {
                    var length = Math.Min(20, frame.Length - offset);
                    using var writer = new DataWriter();
                    writer.WriteBytes(frame.AsSpan(offset, length).ToArray());
                    var status = await _rx.WriteValueAsync(writer.DetachBuffer(), GattWriteOption.WriteWithResponse);
                    if (status != GattCommunicationStatus.Success) throw new IOException($"Phone BLE write failed: {status}.");
                }
            }
            finally { _writeLock.Release(); }
        }

        public async Task<T?> ReceiveAsync<T>(CancellationToken cancellationToken = default)
        {
            if (!await _incoming.Reader.WaitToReadAsync(cancellationToken)) return default;
            var frame = await _incoming.Reader.ReadAsync(cancellationToken);
            return ProtocolJson.Deserialize<T>(frame, _maxMessageBytes);
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
            _incoming.Writer.TryComplete();
            _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
            _tx.ValueChanged -= OnValueChanged;
            _frame.Dispose();
            _writeLock.Dispose();
            _device.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
