using System.Text.Json;
using NodeDeck.Client;
using NodeDeck.Core.Protocol;
using NodeDeckClient.Resources.Strings;
using ProtocolValueType = NodeDeck.Core.Protocol.ValueType;
#if ANDROID
using NodeDeckClient.Platforms.Android;
#endif

namespace NodeDeckClient;

public partial class SessionPage : ContentPage
{
    private readonly ConnectionProfile _profile;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _attempt;
    private CancellationTokenSource? _connected;
    private NodeDeckClientConnection? _client;
    private Task _command = Task.CompletedTask;
    internal Task Completion { get; private set; } = Task.CompletedTask;

    public SessionPage(ConnectionProfile profile)
    {
        InitializeComponent();
        _profile = profile;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (Completion.IsCompleted && !_lifetime.IsCancellationRequested)
            StartAttempt();
    }

    protected override void OnNavigatedFrom(NavigatedFromEventArgs args)
    {
        _lifetime.Cancel();
        base.OnNavigatedFrom(args);
    }

    private async void OnChooseClicked(object? sender, EventArgs e)
    {
        _lifetime.Cancel();
        await Navigation.PopAsync();
    }

    private void OnRetryClicked(object? sender, EventArgs e)
    {
        if (!_lifetime.IsCancellationRequested)
            StartAttempt();
    }

    private void StartAttempt()
    {
        _attempt?.Cancel();
        var attempt = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _attempt = attempt;
        Completion = RunAfterPreviousAsync(Completion, attempt);
    }

    private async Task RunAfterPreviousAsync(Task previous, CancellationTokenSource attempt)
    {
        try
        {
            await previous;
            if (!attempt.IsCancellationRequested)
                await RunSessionAsync(attempt.Token);
        }
        finally
        {
            if (_attempt == attempt)
                _attempt = null;
            attempt.Dispose();
        }
    }

    private void ShowConnection(string title, string status, bool connecting)
    {
        Title = AppResources.Connection;
        CommandsPanel.IsVisible = false;
        ConnectionPanel.IsVisible = true;
        ConnectionTitle.Text = title;
        ConnectionStatus.Text = status;
        Connecting.IsVisible = Connecting.IsRunning = connecting;
    }

    private async Task RunSessionAsync(CancellationToken lifetime)
    {
        var reconnecting = false;
        ShowConnection(_profile.DisplayName, _profile.IsTcp ? AppResources.ConnectingToGateway : AppResources.WaitingForBluetoothGateway, true);
        while (!lifetime.IsCancellationRequested)
        {
            bool wasConnected = false;
            using var active = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
                timeout.CancelAfter(TimeSpan.FromSeconds(_profile.IsTcp ? 15 : 150));
                var connection = await ConnectAsync(timeout.Token);
                await using var client = connection.Client;
                var catalog = (await client.GetCapabilitiesAsync(timeout.Token))
                    .Deserialize<CapabilitiesDocument>(ProtocolJson.Options)
                    ?? throw new InvalidResponseException(AppResources.MissingCommandList);
                lifetime.ThrowIfCancellationRequested();
                ProtocolValidator.ValidateCapabilities(catalog);
                ShowCommands(catalog);
                if (reconnecting)
                    ResultLabel.Text = AppResources.ConnectionRestored;
                _connected = active;
                _client = client;
                wasConnected = true;
                try
                {
                    if (connection.Disconnected is { } disconnected)
                    {
                        await disconnected.WaitAsync(active.Token);
                        throw new IOException(AppResources.GatewayDisconnected);
                    }
                    while (true)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(6), active.Token);
                        if (!_command.IsCompleted)
                            continue;
                        using var probe = CancellationTokenSource.CreateLinkedTokenSource(active.Token);
                        probe.CancelAfter(TimeSpan.FromSeconds(10));
                        await client.GetCapabilitiesAsync(probe.Token);
                    }
                }
                finally
                {
                    _client = null;
                    _connected = null;
                    active.Cancel();
                    await _command;
                }
            }
            catch (Exception) when (lifetime.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (!wasConnected)
                {
                    var reason = ex is OperationCanceledException ? AppResources.GatewayTimeout : ex.Message;
                    ShowConnection(reconnecting ? AppResources.ReconnectionFailed : AppResources.ConnectionFailed, $"{_profile.DisplayName} · {reason}", false);
                    return;
                }
                reconnecting = true;
                ShowConnection(AppResources.ConnectionLost, string.Format(AppResources.ReconnectingToProfile, _profile.DisplayName), true);
            }
        }
    }

    private async Task<(NodeDeckClientConnection Client, Task? Disconnected)> ConnectAsync(CancellationToken token)
    {
        var secret = await SecureStorage.Default.GetAsync(_profile.SecretKey) ?? "nodedeck-dev";
        if (_profile.IsTcp)
            return (await NodeDeckClientConnection.ConnectAsync(_profile.Host!, _profile.Port, secret,
                "android-phone", "NodeDeck Android", token), null);
#if ANDROID
        var ble = await AndroidBleProtocolConnection.ConnectAsync(token);
        return (await NodeDeckClientConnection.ConnectAsync(ble, secret, "android-phone", "NodeDeck Android", token), ble.Disconnected);
#else
        throw new PlatformNotSupportedException(AppResources.BluetoothAndroidOnly);
#endif
    }

    private void ShowCommands(CapabilitiesDocument catalog)
    {
        Title = AppResources.Commands;
        ConnectionPanel.IsVisible = Connecting.IsRunning = false;
        CommandsPanel.IsVisible = true;
        GatewayName.Text = catalog.Gateway.Name;
        ResultLabel.Text = catalog.Commands.Count == 0 ? AppResources.NoCommands : "";
        CommandButtons.Children.Clear();
        MessageEntry.IsVisible = catalog.Commands.Any(UsesMessage);
        foreach (var command in catalog.Commands)
        {
            var button = new Button { Text = command.Title, IsEnabled = command.Availability.Enabled };
            button.Clicked += (_, _) => OnCommandClicked(command);
            CommandButtons.Children.Add(button);
            if (!command.Availability.Enabled && command.Availability.Reason is { } reason)
                CommandButtons.Children.Add(new Label { Text = reason });
        }
    }

    private static bool UsesMessage(CommandDefinition command) =>
        command.Parameters.Properties is { Count: 1 } properties &&
        properties.TryGetValue("message", out var message) && message.Type == ProtocolValueType.String;

    private async void OnCommandClicked(CommandDefinition command)
    {
        if (_client is not { } client || _connected is not { } active || !CommandButtons.IsEnabled)
            return;
        CommandButtons.IsEnabled = false;
        try
        {
            JsonElement arguments;
            if (UsesMessage(command))
                arguments = JsonSerializer.SerializeToElement(new { message = MessageEntry.Text ?? "" });
            else if (command.Parameters.Properties is { Count: > 0 } || command.Parameters.AdditionalProperties == true)
            {
                var fields = command.Parameters.Properties?.Select(p => $"{p.Key} ({p.Value.Type})") ?? [];
                var input = await DisplayPromptAsync(command.Title, string.Format(AppResources.JsonArguments, string.Join(", ", fields)),
                    AppResources.Run, AppResources.Cancel, initialValue: "{}");
                if (input is null)
                    return;
                arguments = JsonSerializer.Deserialize<JsonElement>(input);
            }
            else
                arguments = JsonSerializer.SerializeToElement(new { });

            if (_client != client || active.IsCancellationRequested)
                return;
            var errors = ProtocolValidator.ValidateArguments(command.Parameters, arguments);
            if (errors.Count > 0)
            {
                ResultLabel.Text = string.Join(Environment.NewLine, errors);
                return;
            }
            _command = InvokeAsync(client, command.Id, arguments, active);
            await _command;
        }
        catch (JsonException)
        {
            ResultLabel.Text = AppResources.InvalidJson;
        }
        finally
        {
            CommandButtons.IsEnabled = true;
        }
    }

    private async Task InvokeAsync(NodeDeckClientConnection client, string id, JsonElement arguments, CancellationTokenSource active)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(active.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var response = await client.InvokeAsync(id, arguments, cancellationToken: timeout.Token);
            var execution = response.Deserialize<CommandExecutionResult>(ProtocolJson.Options);
            ResultLabel.Text = execution?.Error?.Message ?? (execution?.Output is { ValueKind: JsonValueKind.Object } output
                && output.TryGetProperty("reply", out var reply) && reply.ValueKind == JsonValueKind.String
                ? reply.GetString() : response.ToString());
        }
        catch (GatewayRequestException ex) when (ex.Code != ProtocolErrorCode.Unauthorized)
        {
            ResultLabel.Text = ex.Message;
        }
        catch (MessageTooLargeException ex)
        {
            ResultLabel.Text = ex.Message;
        }
        catch (Exception)
        {
            // The gateway might already have executed the command. Reconnect without replaying it.
            active.Cancel();
        }
    }
}
