using System.Threading.Channels;
using NodeDeck.Core.Logging;
using NodeDeck.Core.Protocol;
using NodeDeck.Core.Transport;
using NLog;
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

    private readonly BluetoothListenerSettings _settings;
    private readonly ListenerLog _log;

    public WindowsBleProtocolListener() : this(new BluetoothListenerSettings()) { }

    public WindowsBleProtocolListener(BluetoothListenerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(settings.LogLevel);
        if (settings.ServiceUuid == Guid.Empty || settings.ClientToGatewayCharacteristicUuid == Guid.Empty ||
            settings.GatewayToClientCharacteristicUuid == Guid.Empty ||
            settings.ClientToGatewayCharacteristicUuid == settings.GatewayToClientCharacteristicUuid)
            throw new ArgumentException("Configure a BLE service UUID and two distinct characteristic UUIDs.", nameof(settings));
        if (settings.ChunkSize is < 1 or > 512)
            throw new ArgumentOutOfRangeException(nameof(settings.ChunkSize));
        if (settings.InitialMaxMessageBytes is < 1 or > ProtocolConstants.InitialMaxMessageBytes)
            throw new ArgumentOutOfRangeException(nameof(settings.InitialMaxMessageBytes));
        if (!Enum.IsDefined(settings.ScanningMode))
            throw new ArgumentOutOfRangeException(nameof(settings.ScanningMode));
        _settings = settings;
        _log = new ListenerLog(LogManager.GetCurrentClassLogger(), settings.LogLevel);
    }

    public event Action<string>? Diagnostic;

    private void Report(LogLevel level, string message, Exception? error = null)
    {
        _log.Write(level, message, error);
        Diagnostic?.Invoke(message);
    }

    public Task Ready => _ready.Task;

    public void Start()
    {
        if (_started)
            throw new InvalidOperationException("BLE listener has already started.");
        _started = true;
        // Android implementations may put a 128-bit service UUID in the scan response.
        // A strict OS advertisement filter only matches the primary advertisement and
        // can therefore silently miss the phone. Filter the parsed advertisement here.
        _watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = _settings.ScanningMode };
        _watcher.Stopped += (watcher, args) => Report(args.Error == BluetoothError.Success ? LogLevel.Debug : LogLevel.Warn, $"BLE scanner stopped: {args.Error}; watcher status is {watcher.Status}.");
        _watcher.Received += OnAdvertisementReceived;
        _watcher.Start();
        Report(LogLevel.Info, $"BLE scanner {_watcher.Status} in {_settings.ScanningMode} mode for service {_settings.ServiceUuid}.");
    }

    private void OnAdvertisementReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        if (_log.IsEnabled(LogLevel.Trace) || Diagnostic is not null)
        {
            var advertised = string.Join(",", args.Advertisement.ServiceUuids);
            Report(LogLevel.Trace, $"BLE advertisement from {args.BluetoothAddress:X12}; RSSI {args.RawSignalStrengthInDBm}; service UUIDs: {advertised}; name: '{args.Advertisement.LocalName}'.");
        }
        if (!args.Advertisement.ServiceUuids.Contains(_settings.ServiceUuid))
            return;
        Report(LogLevel.Debug, "Found NodeDeck service in advertisement; connecting and discovering GATT service.");
        if (Interlocked.CompareExchange(ref _connecting, 1, 0) == 0)
            _ = ConnectAsync(args.BluetoothAddress);
    }

    private async Task ConnectAsync(ulong address)
    {
        try
        {
            var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address)
                ?? throw new IOException("Could not open the advertising phone over BLE.");
            var services = await device.GetGattServicesForUuidAsync(_settings.ServiceUuid, BluetoothCacheMode.Uncached);
            Report(LogLevel.Debug, $"GATT service discovery result: {services.Status}; matches: {services.Services.Count}.");
            if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
                throw new IOException($"NodeDeck BLE service discovery failed: {services.Status}.");
            var service = services.Services[0];

            var txResult = await service.GetCharacteristicsForUuidAsync(_settings.ClientToGatewayCharacteristicUuid, BluetoothCacheMode.Uncached);
            var rxResult = await service.GetCharacteristicsForUuidAsync(_settings.GatewayToClientCharacteristicUuid, BluetoothCacheMode.Uncached);
            if (txResult.Status != GattCommunicationStatus.Success || txResult.Characteristics.Count == 0 ||
                rxResult.Status != GattCommunicationStatus.Success || rxResult.Characteristics.Count == 0)
                throw new IOException("The phone is missing a NodeDeck BLE characteristic.");

            var tx = txResult.Characteristics[0];
            var rx = rxResult.Characteristics[0];
            var connection = new WindowsBleProtocolConnection(device, rx, tx, _settings);
            tx.ValueChanged += connection.OnValueChanged;
            try
            {
                var cccd = await tx.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify);
                Report(LogLevel.Debug, $"GATT notification subscription result: {cccd}.");
                if (cccd != GattCommunicationStatus.Success)
                    throw new IOException($"Could not subscribe to phone BLE notifications: {cccd}.");
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
            connection.MonitorDisconnection(message => Report(LogLevel.Warn, message));
            _connection = connection;
            _watcher?.Stop();
            _connections.Writer.TryWrite(_connection);
            _ready.TrySetResult();
            Report(LogLevel.Info, "BLE GATT connection is ready.");
        }
        catch (Exception ex)
        {
            Report(LogLevel.Error, $"BLE connection attempt failed: {ex.Message}", ex);
            Interlocked.Exchange(ref _connecting, 0);
            _ready.TrySetException(new IOException("Could not connect to the phone's NodeDeck BLE service. Check that Bluetooth LE mode is active on the phone and that the computer supports BLE central mode.", ex));
        }
    }

    public async Task<IProtocolMessageConnection> AcceptAsync(CancellationToken cancellationToken = default)
    {
        if (!_started)
            throw new InvalidOperationException("Start the BLE listener before accepting a connection.");
        return await _connections.Reader.ReadAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _watcher?.Stop();
        if (_connection is not null)
            await _connection.DisposeAsync();
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
        private int _maxMessageBytes;
        private readonly int _chunkSize;

        public WindowsBleProtocolConnection(BluetoothLEDevice device, GattCharacteristic rx, GattCharacteristic tx, BluetoothListenerSettings settings)
        {
            _device = device;
            _rx = rx;
            _tx = tx;
            _chunkSize = settings.ChunkSize;
            _maxMessageBytes = settings.InitialMaxMessageBytes;
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
            if (maxMessageBytes < 1)
                throw new ArgumentOutOfRangeException(nameof(maxMessageBytes));
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
                    if (value == ProtocolConstants.FrameDelimiter)
                    {
                        if (_frame.Length == 0)
                            continue;
                        var frame = _frame.ToArray();
                        _frame.SetLength(0);
                        if (frame[^1] == ProtocolConstants.CarriageReturn)
                            Array.Resize(ref frame, frame.Length - 1);
                        if (frame.Length > _maxMessageBytes)
                        {
                            _incoming.Writer.TryComplete(new MessageTooLargeException("BLE message exceeds the negotiated limit."));
                            return;
                        }
                        _incoming.Writer.TryWrite(frame);
                    }
                    else
                    {
                        if (_frame.Length >= _maxMessageBytes)
                        {
                            _incoming.Writer.TryComplete(new MessageTooLargeException("BLE message exceeds the negotiated limit."));
                            return;
                        }
                        _frame.WriteByte(value);
                    }
                }
            }
        }

        public async Task SendAsync<T>(T message, CancellationToken cancellationToken = default)
        {
            var frame = ProtocolJson.Serialize(message).Append(ProtocolConstants.FrameDelimiter).ToArray();
            if (frame.Length - 1 > _maxMessageBytes)
                throw new MessageTooLargeException("BLE message exceeds the negotiated limit.");
            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                for (var offset = 0; offset < frame.Length; offset += _chunkSize)
                {
                    var length = Math.Min(_chunkSize, frame.Length - offset);
                    using var writer = new DataWriter();
                    writer.WriteBytes(frame.AsSpan(offset, length).ToArray());
                    var status = await _rx.WriteValueAsync(writer.DetachBuffer(), GattWriteOption.WriteWithResponse);
                    if (status != GattCommunicationStatus.Success)
                        throw new IOException($"Phone BLE write failed: {status}.");
                }
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public async Task<T?> ReceiveAsync<T>(CancellationToken cancellationToken = default)
        {
            if (!await _incoming.Reader.WaitToReadAsync(cancellationToken))
                return default;
            var frame = await _incoming.Reader.ReadAsync(cancellationToken);
            return ProtocolJson.Deserialize<T>(frame, _maxMessageBytes);
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return ValueTask.CompletedTask;
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
