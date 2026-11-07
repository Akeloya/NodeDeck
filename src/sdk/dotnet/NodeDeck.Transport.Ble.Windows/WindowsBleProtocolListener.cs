using System.Threading.Channels;
using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<ulong, WindowsBleProtocolConnection> _peers = new();
    private readonly ConcurrentDictionary<ulong, byte> _addresses = new();
    private readonly object _attemptsLock = new();
    private readonly HashSet<Task> _attempts = [];
    private readonly CancellationTokenSource _diagnosticsCancellation = new();
    private Task? _scanDiagnostics;
    private int _advertisementsReceived;
    private int _matchingAdvertisementsReceived;
    private bool _started;
    private volatile bool _stopped;

    private readonly BluetoothListenerSettings _settings;
    private readonly ListenerLog _log;

    public WindowsBleProtocolListener() : this(new BluetoothListenerSettings()) { }

    public WindowsBleProtocolListener(BluetoothListenerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(settings.LogLevel);
        new BleServiceIdentifiers
        {
            ServiceUuid = settings.ServiceUuid,
            ClientToGatewayCharacteristicUuid = settings.ClientToGatewayCharacteristicUuid,
            GatewayToClientCharacteristicUuid = settings.GatewayToClientCharacteristicUuid
        }.Validate();
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
        _watcher.Stopped += OnScannerStopped;
        _watcher.Received += OnAdvertisementReceived;
        _watcher.Start();
        Report(LogLevel.Info, $"BLE scan requested in {_settings.ScanningMode} mode for service {_settings.ServiceUuid}; initial watcher status: {_watcher.Status}.");
        _scanDiagnostics = ReportScanProgressAsync(_watcher, _diagnosticsCancellation.Token);
        if (_watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started)
            _ready.TrySetResult();
    }

    private void OnScannerStopped(BluetoothLEAdvertisementWatcher watcher, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        var message = $"BLE scanner stopped: {args.Error}; watcher status is {watcher.Status}.";
        Report(args.Error == BluetoothError.Success ? LogLevel.Debug : LogLevel.Error, message);
        if (!_stopped && args.Error != BluetoothError.Success)
        {
            _ready.TrySetException(new IOException(message));
            _connections.Writer.TryComplete(new IOException(message));
        }
    }

    private async Task ReportScanProgressAsync(BluetoothLEAdvertisementWatcher watcher, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            if (_stopped || watcher.Status is BluetoothLEAdvertisementWatcherStatus.Aborted or BluetoothLEAdvertisementWatcherStatus.Stopped)
                return;

            var received = Volatile.Read(ref _advertisementsReceived);
            var matches = Volatile.Read(ref _matchingAdvertisementsReceived);
            Report(LogLevel.Info, $"BLE scan after 10 seconds: status={watcher.Status}; advertisements={received}; NodeDeck advertisements={matches}.");
            if (watcher.Status != BluetoothLEAdvertisementWatcherStatus.Started)
                Report(LogLevel.Warn, "Windows has not started BLE scanning. Check the Bluetooth radio and adapter driver.");
            else if (received == 0)
                Report(LogLevel.Warn, "No BLE advertisements received. Start Bluetooth LE connection on the nearby phone. If the phone reports advertising started, check the PC Bluetooth adapter, driver and reception.");
            else if (matches == 0)
                Report(LogLevel.Warn, "BLE reception works, but no NodeDeck service was advertised. Check that the phone reports BLE advertising started and uses the same service UUID.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private void OnAdvertisementReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        if (_stopped)
            return;
        Interlocked.Increment(ref _advertisementsReceived);
        var matchesService = args.Advertisement.ServiceUuids.Contains(_settings.ServiceUuid);
        if (matchesService)
            Interlocked.Increment(ref _matchingAdvertisementsReceived);
        if (_log.IsEnabled(LogLevel.Trace) || Diagnostic is not null)
        {
            var advertised = string.Join(",", args.Advertisement.ServiceUuids);
            Report(LogLevel.Trace, $"BLE advertisement from {args.BluetoothAddress:X12}; RSSI {args.RawSignalStrengthInDBm}; service UUIDs: {advertised}; name: '{args.Advertisement.LocalName}'.");
        }
        if (!matchesService)
            return;
        if (_addresses.TryAdd(args.BluetoothAddress, 0))
        {
            Report(LogLevel.Info, "Found NodeDeck service in advertisement; connecting and discovering GATT service.");
            lock (_attemptsLock)
            {
                if (_stopped)
                {
                    _addresses.TryRemove(args.BluetoothAddress, out _);
                    return;
                }
                var attempt = ConnectAsync(args.BluetoothAddress);
                _attempts.Add(attempt);
                _ = attempt.ContinueWith(t => { lock (_attemptsLock) _attempts.Remove(t); },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
    }

    private async Task ConnectAsync(ulong address)
    {
        BluetoothLEDevice? device = null;
        GattDeviceService? service = null;
        WindowsBleProtocolConnection? connection = null;
        var handedOff = false;
        var registered = false;
        try
        {
            device = await BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask(_diagnosticsCancellation.Token)
                ?? throw new IOException("Could not open the advertising phone over BLE.");
            var services = await device.GetGattServicesForUuidAsync(_settings.ServiceUuid, BluetoothCacheMode.Uncached)
                .AsTask(_diagnosticsCancellation.Token);
            Report(LogLevel.Info, $"GATT service discovery result: {services.Status}; matches: {services.Services.Count}.");
            if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
                throw new IOException($"NodeDeck BLE service discovery failed: {services.Status}.");
            service = services.Services[0];
            foreach (var extra in services.Services.Skip(1))
                extra.Dispose();

            var txResult = await service.GetCharacteristicsForUuidAsync(_settings.ClientToGatewayCharacteristicUuid, BluetoothCacheMode.Uncached)
                .AsTask(_diagnosticsCancellation.Token);
            var rxResult = await service.GetCharacteristicsForUuidAsync(_settings.GatewayToClientCharacteristicUuid, BluetoothCacheMode.Uncached)
                .AsTask(_diagnosticsCancellation.Token);
            if (txResult.Status != GattCommunicationStatus.Success || txResult.Characteristics.Count == 0 ||
                rxResult.Status != GattCommunicationStatus.Success || rxResult.Characteristics.Count == 0)
                throw new IOException("The phone is missing a NodeDeck BLE characteristic.");

            var tx = txResult.Characteristics[0];
            var rx = rxResult.Characteristics[0];
            connection = new WindowsBleProtocolConnection(device, service, rx, tx, _settings, closed => OnConnectionClosed(address, closed));
            tx.ValueChanged += connection.OnValueChanged;
            try
            {
                var cccd = await tx.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify)
                    .AsTask(_diagnosticsCancellation.Token);
                Report(LogLevel.Info, $"GATT notification subscription result: {cccd}.");
                if (cccd != GattCommunicationStatus.Success)
                    throw new IOException($"Could not subscribe to phone BLE notifications: {cccd}.");
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
            _diagnosticsCancellation.Token.ThrowIfCancellationRequested();
            _peers[address] = connection;
            registered = true;
            connection.MonitorDisconnection(message => Report(LogLevel.Warn, message));
            // Keep watching so a phone can advertise again after a disconnect.
            if (!_connections.Writer.TryWrite(connection))
            {
                await connection.DisposeAsync();
                return;
            }
            handedOff = true;
            _ready.TrySetResult();
            Report(LogLevel.Info, "BLE GATT connection is ready.");
        }
        catch (OperationCanceledException) when (_stopped) { }
        catch (Exception ex)
        {
            Report(LogLevel.Error, $"BLE connection attempt failed: {ex.Message}", ex);
        }
        finally
        {
            if (!handedOff)
            {
                if (connection is not null)
                    await connection.DisposeAsync();
                else
                {
                    service?.Dispose();
                    device?.Dispose();
                }
                if (!registered)
                    _addresses.TryRemove(address, out _);
            }
        }
    }

    private void OnConnectionClosed(ulong address, WindowsBleProtocolConnection connection)
    {
        if (_peers.TryRemove(new KeyValuePair<ulong, WindowsBleProtocolConnection>(address, connection)))
            _addresses.TryRemove(address, out _);
    }

    public async Task<IProtocolMessageConnection> AcceptAsync(CancellationToken cancellationToken = default)
    {
        if (!_started)
            throw new InvalidOperationException("Start the BLE listener before accepting a connection.");
        return await _connections.Reader.ReadAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_stopped)
            return;
        _stopped = true;
        _diagnosticsCancellation.Cancel();
        if (_scanDiagnostics is not null)
            await _scanDiagnostics;
        _connections.Writer.TryComplete();
        _watcher?.Stop();
        Task[] attempts;
        lock (_attemptsLock)
            attempts = _attempts.ToArray();
        await Task.WhenAll(attempts);
        foreach (var connection in _peers.Values)
            await connection.DisposeAsync();
        if (_watcher is not null)
        {
            _watcher.Received -= OnAdvertisementReceived;
            _watcher.Stopped -= OnScannerStopped;
        }
        _diagnosticsCancellation.Dispose();
    }

    private sealed class WindowsBleProtocolConnection : IProtocolMessageConnection
    {
        private readonly BluetoothLEDevice _device;
        private readonly GattDeviceService _service;
        private readonly Action<WindowsBleProtocolConnection> _closed;
        private readonly GattCharacteristic _rx;
        private readonly GattCharacteristic _tx;
        private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        private readonly MemoryStream _frame = new();
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly object _frameLock = new();
        private int _maxMessageBytes;
        private readonly int _chunkSize;

        public WindowsBleProtocolConnection(BluetoothLEDevice device, GattDeviceService service, GattCharacteristic rx,
            GattCharacteristic tx, BluetoothListenerSettings settings, Action<WindowsBleProtocolConnection> closed)
        {
            _device = device;
            _service = service;
            _closed = closed;
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
            using var reader = DataReader.FromBuffer(args.CharacteristicValue);
            var bytes = new byte[args.CharacteristicValue.Length];
            reader.ReadBytes(bytes);
            lock (_frameLock)
            {
                if (Volatile.Read(ref _disposed) != 0)
                    return;
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
                    var status = await _rx.WriteValueAsync(writer.DetachBuffer(), GattWriteOption.WriteWithResponse).AsTask(cancellationToken);
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
            lock (_frameLock)
                _frame.Dispose();
            _service.Dispose();
            _device.Dispose();
            _closed(this);
            return ValueTask.CompletedTask;
        }
    }
}
