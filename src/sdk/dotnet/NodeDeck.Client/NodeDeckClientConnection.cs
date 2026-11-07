using System.Text.Json;
using System.Collections.Concurrent;
using NodeDeck.Core.Protocol;
using NodeDeck.Core.Transport;
using NodeDeck.Transport.Tcp;

namespace NodeDeck.Client;

public sealed class NodeDeckClientConnection : IAsyncDisposable
{
    private readonly IProtocolMessageConnection _connection;
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private string? _accessToken;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ProtocolResponse>> _pending = new();
    private readonly Task _receiver;
    private readonly TaskCompletionSource _disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;
    public Task Disconnected => _disconnected.Task;
    private string _protocolVersion = ProtocolConstants.CurrentVersion;
    private GatewayRoute? _route;

    private NodeDeckClientConnection(IProtocolMessageConnection connection)
    {
        _connection = connection;
        _receiver = ReceiveAsync();
    }

    public static async Task<NodeDeckClientConnection> ConnectAsync(string host, int port, string sharedSecret,
        string? clientId = null, string clientName = "NodeDeck client", CancellationToken cancellationToken = default, string? expectedGatewayId = null,
        GatewayRoute? route = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sharedSecret);
        var connection = await TcpJsonConnection.ConnectAsync(host, port, cancellationToken: cancellationToken).ConfigureAwait(false);
        return await ConnectAsync(connection, sharedSecret, clientId, clientName, cancellationToken, expectedGatewayId, route).ConfigureAwait(false);
    }

    public static async Task<NodeDeckClientConnection> ConnectAsync(IProtocolMessageConnection connection, string sharedSecret,
        string? clientId = null, string clientName = "NodeDeck client", CancellationToken cancellationToken = default, string? expectedGatewayId = null,
        GatewayRoute? route = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(sharedSecret);
        var client = new NodeDeckClientConnection(connection) { _route = route };
        try
        {
            var hello = await client.SendRequestAsync(RequestType.ProtocolHello, new HelloPayload([ProtocolConstants.CurrentVersion], new ClientInfo(clientId ?? Guid.NewGuid().ToString("N"), clientName)), false, cancellationToken).ConfigureAwait(false);
            var helloResult = hello.Deserialize<HelloResult>(ProtocolJson.Options) ?? throw new InvalidResponseException("Gateway returned invalid hello response.");
            if (helloResult.SelectedVersion != ProtocolConstants.CurrentVersion)
                throw new UnsupportedVersionException($"Gateway selected {helloResult.SelectedVersion}.");
            if (expectedGatewayId is not null && helloResult.GatewayId != expectedGatewayId)
                throw new InvalidResponseException($"Expected gateway '{expectedGatewayId}', received '{helloResult.GatewayId}'.");
            client._protocolVersion = helloResult.SelectedVersion;
            if (helloResult.MaxMessageBytes < ProtocolConstants.InitialMaxMessageBytes)
                throw new InvalidResponseException("Gateway advertised an invalid message limit.");
            connection.SetMaxMessageBytes(helloResult.MaxMessageBytes);
            var login = await client.SendRequestAsync(RequestType.AuthLogin, new LoginPayload(AuthMethod.SharedSecret, sharedSecret), false, cancellationToken).ConfigureAwait(false);
            client._accessToken = (login.Deserialize<LoginResult>(ProtocolJson.Options) ?? throw new InvalidResponseException("Gateway returned invalid login response.")).AccessToken;
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task<JsonElement> GetCapabilitiesAsync(CancellationToken cancellationToken = default) =>
        SendRequestAsync(RequestType.CapabilitiesGet, new { }, true, cancellationToken);

    public Task<JsonElement> GetUiAsync(CancellationToken cancellationToken = default) =>
        SendRequestAsync(RequestType.UiGet, new { }, true, cancellationToken);

    public Task<JsonElement> InvokeAsync(string commandId, object arguments, string? uiRevision = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        ArgumentNullException.ThrowIfNull(arguments);
        return SendRequestAsync(RequestType.CommandInvoke, new CommandInvokePayload(commandId, JsonSerializer.SerializeToElement(arguments, ProtocolJson.Options), uiRevision), true, cancellationToken);
    }

    public Task<JsonElement> GetOperationAsync(string operationToken, CancellationToken cancellationToken = default) =>
        SendRequestAsync(RequestType.OperationGet, new OperationGetPayload(operationToken), true, cancellationToken);

    /// <summary>Forwards an application request without changing its idempotency key or exposing the previous hop's token.</summary>
    public Task<JsonElement> ForwardAsync(ProtocolRequest request, GatewayRoute route, CancellationToken cancellationToken = default)
    {
        if (request.Type is not (RequestType.CapabilitiesGet or RequestType.UiGet or RequestType.CommandInvoke or RequestType.OperationGet))
            throw new ArgumentException("Only application requests can be forwarded.", nameof(request));
        return SendRequestAsync(request.Type, request.Payload, true, cancellationToken, request.RequestId, route);
    }

    private async Task ReceiveAsync()
    {
        Exception failure = new IOException("Gateway closed the connection.");
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var frame = await _connection.ReceiveAsync<JsonElement>(_lifetime.Token).ConfigureAwait(false);
                if (frame.ValueKind == JsonValueKind.Undefined)
                    break;
                if (frame.ValueKind != JsonValueKind.Object)
                    throw new InvalidResponseException("Gateway sent a non-object frame.");
                if (frame.TryGetProperty("payload", out _))
                {
                    var ping = frame.Deserialize<ProtocolRequest>(ProtocolJson.Options)!;
                    ProtocolValidator.ValidateRequest(ping, helloCompleted: true, _connection.MaxMessageBytes);
                    if (ping.Type != RequestType.SessionPing || ping.AccessToken is not null)
                        throw new InvalidResponseException("Unexpected gateway request.");
                    await _connection.SendAsync(ProtocolResponse.Success(ping, new { }), _lifetime.Token).ConfigureAwait(false);
                    continue;
                }
                var response = frame.Deserialize<ProtocolResponse>(ProtocolJson.Options)!;
                ProtocolValidator.ValidateResponse(response, _connection.MaxMessageBytes);
                if (_pending.TryGetValue(response.RequestId, out var completion))
                    completion.TrySetResult(response);
                else
                    throw new InvalidResponseException("Gateway response has an unknown requestId.");
            }
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            _lifetime.Cancel();
            foreach (var completion in _pending.Values)
                completion.TrySetException(failure);
            _disconnected.TrySetResult();
        }
    }

    private async Task<JsonElement> SendRequestAsync(string type, object payload, bool authenticated, CancellationToken cancellationToken,
        string? originalRequestId = null, GatewayRoute? route = null)
    {
        await _requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? requestId = null;
        try
        {
            if (_lifetime.IsCancellationRequested)
                throw new IOException("Gateway connection is closed. Reconnect to continue.");
            var request = new ProtocolRequest(_protocolVersion, originalRequestId ?? Guid.NewGuid().ToString("N"), type,
                JsonSerializer.SerializeToElement(payload, ProtocolJson.Options), authenticated ? _accessToken : null, route ?? _route);
            ProtocolValidator.ValidateRequest(request, helloCompleted: type != RequestType.ProtocolHello, maxMessageBytes: _connection.MaxMessageBytes);
            requestId = request.RequestId;
            var completion = new TaskCompletionSource<ProtocolResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[requestId] = completion;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            await _connection.SendAsync(request, linked.Token).ConfigureAwait(false);
            var response = await completion.Task.WaitAsync(linked.Token).ConfigureAwait(false);
            if (response.Type != request.Type || response.ProtocolVersion != _protocolVersion)
                throw new InvalidResponseException("Gateway response correlation does not match the request.");
            if (response.Error is { } error)
            {
                if (error.Code == ProtocolErrorCode.Unauthorized)
                    _accessToken = null;
                throw new GatewayRequestException(error);
            }
            return response.Result ?? throw new InvalidResponseException("Gateway response omitted result.");
        }
        catch (OperationCanceledException)
        {
            // A partial write cannot safely be reused or retried automatically.
            _lifetime.Cancel();
            throw;
        }
        finally
        {
            if (requestId is not null)
                _pending.TryRemove(requestId, out _);
            _requestLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        if (_accessToken is not null && !_lifetime.IsCancellationRequested)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await SendRequestAsync(RequestType.AuthLogout, new { }, true, timeout.Token).ConfigureAwait(false);
            }
            catch { }
        }
        _lifetime.Cancel();
        await _receiver.ConfigureAwait(false);
        await _connection.DisposeAsync().ConfigureAwait(false);
    }
}

public sealed class GatewayRequestException(ProtocolError error) : NodeDeckProtocolException(error.Code, $"{error.Code}: {error.Message}")
{
    public ProtocolError Error { get; } = error;
}
