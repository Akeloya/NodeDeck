using System.Threading.Channels;
using Android;
using Android.App;
using Android.Bluetooth;
using Android.Bluetooth.LE;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Java.Util;
using NodeDeck.Core.Protocol;
using NodeDeck.Core.Transport;

namespace NodeDeckClient.Platforms.Android;

/// <summary>Hosts the NodeDeck GATT service on the phone so Windows can connect as a BLE central.</summary>
internal sealed class AndroidBleProtocolConnection : IProtocolMessageConnection
{
    private const int WaitForGatewaySeconds = 120;
    public static event Action<string>? Diagnostic;
    private readonly BluetoothGattServer _server;
    private readonly GattServerCallback _callback;
    private readonly BluetoothLeAdvertiser _advertiser;
    private readonly AdvertiseCallback _advertiseCallback;
    private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly MemoryStream _frame = new();
    private readonly SemaphoreSlim _notifyLock = new(1, 1);
    private readonly object _frameLock = new();
    private bool _closed;
    private int _maxMessageBytes = ProtocolConstants.InitialMaxMessageBytes;

    private AndroidBleProtocolConnection(BluetoothGattServer server, GattServerCallback callback,
        BluetoothLeAdvertiser advertiser, AdvertiseCallback advertiseCallback)
    {
        _server = server;
        _callback = callback;
        _advertiser = advertiser;
        _advertiseCallback = advertiseCallback;
    }

    public Task Disconnected => _callback.Disconnected.Task;

    public int MaxMessageBytes => _maxMessageBytes;
    public void SetMaxMessageBytes(int maxMessageBytes)
    {
        if (maxMessageBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(maxMessageBytes));
        _maxMessageBytes = maxMessageBytes;
    }

    public static async Task<AndroidBleProtocolConnection> ConnectAsync(CancellationToken cancellationToken = default)
    {
        var activity = Platform.CurrentActivity ?? throw new InvalidOperationException("Android activity is unavailable.");
        await AndroidBlePermissions.RequestAsync(activity, cancellationToken);
        var manager = (BluetoothManager?)activity.GetSystemService(Context.BluetoothService)
            ?? throw new InvalidOperationException("Android Bluetooth is unavailable.");
        var adapter = manager.Adapter ?? throw new InvalidOperationException("This phone does not support Bluetooth.");
        if (!adapter.IsEnabled)
            throw new InvalidOperationException("Turn on Bluetooth, then tap Connect again.");
        var advertiser = adapter.BluetoothLeAdvertiser ?? throw new InvalidOperationException("This phone does not support BLE advertising.");

        var callback = new GattServerCallback();
        var server = manager.OpenGattServer(activity, callback)
            ?? throw new IOException("Android could not open a BLE GATT server.");
        var connection = new AndroidBleProtocolConnection(server, callback, advertiser, new ServiceAdvertiseCallback());
        try
        {
            callback.Connection = connection;
            var service = new BluetoothGattService(UUID.FromString(BleProtocolConstants.ServiceUuid), GattServiceType.Primary);
            Diagnostic?.Invoke("Opening GATT server and registering NodeDeck service…");

            var tx = new BluetoothGattCharacteristic(UUID.FromString(BleProtocolConstants.ClientToGatewayCharacteristicUuid),
                GattProperty.Notify, GattPermission.Read);
            tx.AddDescriptor(new BluetoothGattDescriptor(UUID.FromString(BleProtocolConstants.ClientConfigurationDescriptorUuid),
                GattDescriptorPermission.Read | GattDescriptorPermission.Write));
            var rx = new BluetoothGattCharacteristic(UUID.FromString(BleProtocolConstants.GatewayToClientCharacteristicUuid),
                GattProperty.Write, GattPermission.Write);
            service.AddCharacteristic(tx);
            service.AddCharacteristic(rx);

            callback.SetCharacteristics(tx, rx);
            if (!server.AddService(service))
                throw new IOException("Android rejected the NodeDeck GATT service.");
            await callback.ServiceAdded.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            Diagnostic?.Invoke("NodeDeck GATT service registered; starting BLE advertising…");

            var data = new AdvertiseData.Builder()!
                // A 128-bit service UUID already consumes 18 of the legacy 31-byte
                // advertisement payload bytes. Do not include the device name here:
                // Android rejects the packet with ADVERTISE_FAILED_DATA_TOO_LARGE.
                .SetIncludeDeviceName(false)!
                .AddServiceUuid(new ParcelUuid(UUID.FromString(BleProtocolConstants.ServiceUuid)))!
                .Build()!;
            var settings = new AdvertiseSettings.Builder()!
                .SetAdvertiseMode(AdvertiseMode.LowLatency)!
                .SetConnectable(true)!
                .SetTimeout(0)!
                .SetTxPowerLevel(AdvertiseTx.PowerMedium)!
                .Build()!;
            advertiser.StartAdvertising(settings, data, connection._advertiseCallback);
            await ((ServiceAdvertiseCallback)connection._advertiseCallback).Started.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            Diagnostic?.Invoke("BLE advertising started; waiting for computer to connect…");
            await callback.Connected.Task.WaitAsync(TimeSpan.FromSeconds(WaitForGatewaySeconds), cancellationToken);
            Diagnostic?.Invoke("BLE notifications enabled. Connecting to gateway…");
            return connection;
        }
        catch (TimeoutException ex)
        {
            await connection.DisposeAsync();
            throw new TimeoutException("Timed out while setting up the NodeDeck BLE connection.", ex);
        }
        catch (Exception ex)
        {
            await connection.DisposeAsync();
            Diagnostic?.Invoke($"BLE connection failed: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    public async Task SendAsync<T>(T message, CancellationToken cancellationToken = default)
    {
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(message, ProtocolJson.Options).Append(ProtocolConstants.FrameDelimiter).ToArray();
        if (bytes.Length - 1 > _maxMessageBytes)
            throw new MessageTooLargeException("BLE message exceeds the negotiated limit.");
        var device = _callback.Central ?? throw new IOException("The computer is not connected to the phone's BLE service.");
        var characteristic = _callback.Tx ?? throw new IOException("The NodeDeck BLE transmit characteristic is unavailable.");
        await _notifyLock.WaitAsync(cancellationToken);
        try
        {
            for (var offset = 0; offset < bytes.Length; offset += BleProtocolConstants.ChunkSize)
            {
                var length = Math.Min(BleProtocolConstants.ChunkSize, bytes.Length - offset);
                var chunk = bytes.AsSpan(offset, length).ToArray();
                _callback.BeginNotification();
                bool started;
#pragma warning disable CA1416 // Use the API 33 overload only on Android 13 and newer.
                if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
                {
                    started = NotifyApi33(device, characteristic, chunk);
                }
                else
                {
                    started = NotifyLegacy(device, characteristic, chunk);
                }
#pragma warning restore CA1416
                if (!started)
                    throw new IOException("Android could not send a BLE notification to the computer.");
                await _callback.WaitForNotificationAsync(cancellationToken);
            }
        }
        finally
        {
            _notifyLock.Release();
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("android33.0")]
    private bool NotifyApi33(BluetoothDevice device, BluetoothGattCharacteristic characteristic, byte[] value) =>
        _server.NotifyCharacteristicChanged(device, characteristic, false, value) == (int)CurrentBluetoothStatusCodes.Success;

    private bool NotifyLegacy(BluetoothDevice device, BluetoothGattCharacteristic characteristic, byte[] value)
    {
#pragma warning disable CA1422 // Use the legacy notification API only on Android versions before API 33.
        characteristic.SetValue(value);
        return _server.NotifyCharacteristicChanged(device, characteristic, false);
#pragma warning restore CA1422
    }

    public async Task<T?> ReceiveAsync<T>(CancellationToken cancellationToken = default)
    {
        var bytes = await _incoming.Reader.ReadAsync(cancellationToken);
        return ProtocolJson.Deserialize<T>(bytes, _maxMessageBytes);
    }

    private void AcceptWrite(byte[] bytes)
    {
        lock (_frameLock)
        {
            if (_closed)
                return;
            foreach (var value in bytes)
            {
                if (value == ProtocolConstants.FrameDelimiter)
                {
                    if (_frame.Length == 0)
                        continue;
                    var message = _frame.ToArray();
                    _frame.SetLength(0);
                    if (message[^1] == ProtocolConstants.CarriageReturn)
                        Array.Resize(ref message, message.Length - 1);
                    if (message.Length > _maxMessageBytes)
                    {
                        _incoming.Writer.TryComplete(new MessageTooLargeException("BLE message exceeds the negotiated limit."));
                        return;
                    }
                    _incoming.Writer.TryWrite(message);
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

    public ValueTask DisposeAsync()
    {
        lock (_frameLock)
        {
            if (_closed)
                return ValueTask.CompletedTask;
            _closed = true;
            _frame.Dispose();
        }
        _callback.Connection = null;
        _incoming.Writer.TryComplete();
        _advertiser.StopAdvertising(_advertiseCallback);
        _server.Close();
        _notifyLock.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed class ServiceAdvertiseCallback : AdvertiseCallback
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override void OnStartSuccess(AdvertiseSettings? settingsInEffect) => Started.TrySetResult(true);
        public override void OnStartFailure(AdvertiseFailure errorCode) => Started.TrySetException(new IOException($"BLE advertising failed: {errorCode}."));
    }

    private sealed class GattServerCallback : BluetoothGattServerCallback
    {
        private TaskCompletionSource<bool>? _notificationSent;
        public AndroidBleProtocolConnection? Connection { get; set; }
        public TaskCompletionSource<bool> ServiceAdded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disconnected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Connected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public BluetoothDevice? Central { get; private set; }
        public BluetoothGattCharacteristic? Tx { get; private set; }
        public BluetoothGattCharacteristic? Rx { get; private set; }

        public void SetCharacteristics(BluetoothGattCharacteristic tx, BluetoothGattCharacteristic rx)
        {
            Tx = tx;
            Rx = rx;
        }
        public void BeginNotification() => _notificationSent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task WaitForNotificationAsync(CancellationToken cancellationToken) =>
            (_notificationSent ?? throw new InvalidOperationException("No BLE notification is pending.")).Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

        public override void OnServiceAdded(GattStatus status, BluetoothGattService? service)
        {
            Diagnostic?.Invoke($"Android GATT service registration callback: {status}.");
            if (status == GattStatus.Success)
                ServiceAdded.TrySetResult(true);
            else
                ServiceAdded.TrySetException(new IOException($"Could not add NodeDeck BLE service: {status}."));
        }

        public override void OnConnectionStateChange(BluetoothDevice? device, ProfileState status, ProfileState newState)
        {
            Diagnostic?.Invoke($"GATT connection callback: status={status}, state={newState}, device={device?.Address ?? "unknown"}.");
            if ((int)status != 0 || newState == ProfileState.Disconnected)
            {
                Central = null;
                var error = new IOException($"Windows BLE gateway disconnected ({status}).");
                Disconnected.TrySetResult();
                Connected.TrySetException(error);
                _notificationSent?.TrySetException(error);
                Connection?._incoming.Writer.TryComplete(error);
                return;
            }
            if (newState == ProfileState.Connected && device is not null)
            {
                Central = device;
                Diagnostic?.Invoke("Computer connected over GATT. Waiting for notification subscription…");
            }
        }

        public override void OnDescriptorWriteRequest(BluetoothDevice? device, int requestId, BluetoothGattDescriptor? descriptor,
            bool preparedWrite, bool responseNeeded, int offset, byte[]? value)
        {
            if (device is null)
                return;
            var subscribed = !preparedWrite && offset == 0 && value is [1, 0]
                && descriptor?.Characteristic?.Uuid?.Equals(Tx?.Uuid) == true;
            if (responseNeeded)
                Connection?._server.SendResponse(device, requestId, GattStatus.Success, offset, value ?? []);
            if (subscribed)
            {
                Central = device;
                Connected.TrySetResult(true);
            }
        }

        public override void OnCharacteristicWriteRequest(BluetoothDevice? device, int requestId, BluetoothGattCharacteristic? characteristic,
            bool preparedWrite, bool responseNeeded, int offset, byte[]? value)
        {
            if (device is not null && characteristic?.Uuid == Rx?.Uuid && value is not null)
            {
                Central = device;
                Connection?.AcceptWrite(value);
            }
            if (responseNeeded && device is not null)
                Connection?._server.SendResponse(device, requestId, GattStatus.Success, offset, value ?? []);
        }

        public override void OnNotificationSent(BluetoothDevice? device, GattStatus status)
        {
            if (status == GattStatus.Success)
                _notificationSent?.TrySetResult(true);
            else
                _notificationSent?.TrySetException(new IOException($"BLE notification failed: {status}."));
        }
    }
}

internal static class AndroidBlePermissions
{
    private const int RequestCode = 7124;
    private static TaskCompletionSource<bool>? _pending;

    public static async Task RequestAsync(Activity activity, CancellationToken cancellationToken)
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.M)
            return;
        if (Build.VERSION.SdkInt < BuildVersionCodes.S)
            return;
        var permissions = new[] { "android.permission.BLUETOOTH_CONNECT", "android.permission.BLUETOOTH_ADVERTISE" };
        if (permissions.All(x => activity.CheckSelfPermission(x) == Permission.Granted))
            return;
        if (_pending is not null)
        {
            await _pending.Task.WaitAsync(TimeSpan.FromMinutes(1), cancellationToken);
            return;
        }
        _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        activity.RequestPermissions(permissions, RequestCode);
        await _pending.Task.WaitAsync(TimeSpan.FromMinutes(1), cancellationToken);
    }

    public static void Complete(int requestCode, Permission[] grantResults)
    {
        if (requestCode != RequestCode)
            return;
        if (grantResults.All(x => x == Permission.Granted))
            _pending?.TrySetResult(true);
        else
            _pending?.TrySetException(new UnauthorizedAccessException("Allow Nearby devices permission to advertise the NodeDeck BLE service."));
        _pending = null;
    }
}
