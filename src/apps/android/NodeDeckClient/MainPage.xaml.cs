using System.Text.Json;
using NodeDeck.Client;
using NodeDeck.Core.Protocol;
#if ANDROID
using NodeDeckClient.Platforms.Android;
#endif

namespace NodeDeckClient;

public partial class MainPage : ContentPage
{
    private NodeDeckClientConnection? _client;
    private bool _busy;

    public MainPage()
    {
        InitializeComponent();
        OnTransportChanged(this, EventArgs.Empty);
#if ANDROID
        AndroidBleProtocolConnection.Diagnostic += OnBleDiagnostic;
#endif
    }

#if ANDROID
    private void OnBleDiagnostic(string message)
    {
        MainThread.BeginInvokeOnMainThread(() => StatusLabel.Text = message);
    }
#endif

    protected override async void OnDisappearing()
    {
        await DisconnectAsync();
        base.OnDisappearing();
    }

    private void OnTransportChanged(object? sender, EventArgs e)
    {
        var isTcp = TransportPicker.SelectedIndex == 0;
        HostEntry.IsVisible = PortEntry.IsVisible = TcpHelpLabel.IsVisible = isTcp;
        if (!isTcp)
            TcpHelpLabel.Text = "This phone advertises the NodeDeck BLE service. Start the Console Bluetooth LE profile, then tap Connect.";
        else
            TcpHelpLabel.Text = "Enter the computer's LAN IP. For an Android emulator use 10.0.2.2 when the gateway runs on the host.";
    }

    private async void OnConnectClicked(object? sender, EventArgs e)
    {
        await RunBusyAsync(async () =>
        {
            CommandButtons.IsEnabled = false;
            await DisconnectAsync();
            if (TransportPicker.SelectedIndex == 0)
            {
                if (!int.TryParse(PortEntry.Text, out var port) || port is < 1 or > 65535)
                    throw new ArgumentException("Port must be between 1 and 65535.");
                _client = await NodeDeckClientConnection.ConnectAsync(HostEntry.Text?.Trim() ?? "", port,
                    SecretEntry.Text ?? "", "android-phone", "NodeDeck Android");
            }
            else
            {
#if ANDROID
                StatusLabel.Text = "Advertising NodeDeck BLE service; waiting for the Console gateway…";
                var bleConnection = await AndroidBleProtocolConnection.ConnectAsync();
                _client = await NodeDeckClientConnection.ConnectAsync(bleConnection, SecretEntry.Text ?? "",
                    "android-phone", "NodeDeck Android");
#else
                throw new PlatformNotSupportedException("Bluetooth LE client is available on Android.");
#endif
            }
            var capabilities = await _client.GetCapabilitiesAsync();
            var count = capabilities.GetProperty(ProtocolField.Commands).GetArrayLength();
            CommandButtons.IsEnabled = true;
            StatusLabel.Text = $"Connected. Gateway publishes {count} command(s).";
        });
    }

    private async void OnCommandClicked(object? sender, EventArgs e)
    {
        if (sender is not Button button || button.CommandParameter is not string commandId)
            return;
        if (_client is null)
        {
            StatusLabel.Text = "Connect to a gateway first.";
            return;
        }
        await RunBusyAsync(async () =>
        {
            var response = await _client.InvokeAsync(commandId, new { message = MessageEntry.Text ?? "" });
            var execution = response.Deserialize<CommandExecutionResult>(ProtocolJson.Options);
            if (execution?.Error is { } error)
                throw new GatewayRequestException(error);
            ResultLabel.Text = execution?.Output is { } output && output.TryGetProperty("reply", out var reply)
                ? reply.GetString() ?? "Command completed."
                : JsonSerializer.Serialize(response);
            StatusLabel.Text = $"Command {commandId}: {execution?.Status ?? ExecutionStatus.Completed}.";
        });
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        if (_busy)
            return;
        _busy = true;
        ConnectButton.IsEnabled = false;
        StatusLabel.Text = "Working…";
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Error";
            ResultLabel.Text = ex.ToString();
        }
        finally
        {
            _busy = false;
            ConnectButton.IsEnabled = true;
        }
    }

    private async Task DisconnectAsync()
    {
        CommandButtons.IsEnabled = false;
        if (_client is null)
            return;
        await _client.DisposeAsync();
        _client = null;
    }
}
