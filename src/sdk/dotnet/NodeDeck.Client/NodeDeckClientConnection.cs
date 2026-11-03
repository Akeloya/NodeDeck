using System.Text.Json;
using NodeDeck.Protocol;
using NodeDeck.Transport.Tcp;

namespace NodeDeck.Client;

public sealed class NodeDeckClientConnection : IAsyncDisposable
{
    private readonly IProtocolMessageConnection _connection;
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private string? _accessToken;
    private string _protocolVersion = ProtocolConstants.CurrentVersion;

    private NodeDeckClientConnection(IProtocolMessageConnection connection) => _connection = connection;

    public static async Task<NodeDeckClientConnection> ConnectAsync(string host, int port, string sharedSecret,
        string clientId = "dotnet-client", string clientName = "NodeDeck client", CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sharedSecret);
        var connection = await TcpJsonConnection.ConnectAsync(host, port, cancellationToken: cancellationToken).ConfigureAwait(false);
        return await ConnectAsync(connection, sharedSecret, clientId, clientName, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<NodeDeckClientConnection> ConnectAsync(IProtocolMessageConnection connection, string sharedSecret,
        string clientId = "dotnet-client", string clientName = "NodeDeck client", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(sharedSecret);
        var client = new NodeDeckClientConnection(connection);
        try
        {
            var hello = await client.SendRequestAsync(RequestType.ProtocolHello, new HelloPayload([ProtocolConstants.CurrentVersion], new ClientInfo(clientId, clientName)), false, cancellationToken).ConfigureAwait(false);
            var helloResult = hello.Deserialize<HelloResult>(ProtocolJson.Options) ?? throw new InvalidResponseException("Gateway returned invalid hello response.");
            if (helloResult.SelectedVersion != ProtocolConstants.CurrentVersion)
                throw new UnsupportedVersionException($"Gateway selected {helloResult.SelectedVersion}.");
            client._protocolVersion = helloResult.SelectedVersion;
            if (helloResult.MaxMessageBytes < ProtocolConstants.InitialMaxMessageBytes)
                throw new InvalidResponseException("Gateway advertised an invalid message limit.");
            connection.SetMaxMessageBytes(helloResult.MaxMessageBytes);
            var login = await client.SendRequestAsync(RequestType.AuthLogin, new LoginPayload(AuthMethod.SharedSecret, sharedSecret), false, cancellationToken).ConfigureAwait(false);
            client._accessToken = (login.Deserialize<LoginResult>(ProtocolJson.Options) ?? throw new InvalidResponseException("Gateway returned invalid login response.")).AccessToken;
            return client;
        }
        catch { await client.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public Task<JsonElement> GetCapabilitiesAsync(CancellationToken cancellationToken = default) =>
        SendRequestAsync(RequestType.CapabilitiesGet, new { }, true, cancellationToken);

    public Task<JsonElement> GetUiAsync(CancellationToken cancellationToken = default) =>
        SendRequestAsync(RequestType.UiGet, new { }, true, cancellationToken);

    public Task<JsonElement> InvokeAsync(string commandId, object arguments, string? uiRevision = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId); ArgumentNullException.ThrowIfNull(arguments);
        return SendRequestAsync(RequestType.CommandInvoke, new CommandInvokePayload(commandId, JsonSerializer.SerializeToElement(arguments, ProtocolJson.Options), uiRevision), true, cancellationToken);
    }

    public Task<JsonElement> GetOperationAsync(string operationToken, CancellationToken cancellationToken = default) =>
        SendRequestAsync(RequestType.OperationGet, new OperationGetPayload(operationToken), true, cancellationToken);

    private async Task<JsonElement> SendRequestAsync(string type, object payload, bool authenticated, CancellationToken cancellationToken)
    {
        await _requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var request = new ProtocolRequest(_protocolVersion, Guid.NewGuid().ToString("N"), type,
                JsonSerializer.SerializeToElement(payload, ProtocolJson.Options), authenticated ? _accessToken : null);
            ProtocolValidator.ValidateRequest(request, helloCompleted: type != RequestType.ProtocolHello, maxMessageBytes: _connection.MaxMessageBytes);
            await _connection.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var response = await _connection.ReceiveAsync<ProtocolResponse>(cancellationToken).ConfigureAwait(false)
                ?? throw new IOException("Gateway closed the TCP connection.");
            if (response.RequestId != request.RequestId || response.Type != request.Type)
                throw new InvalidResponseException("Gateway response correlation does not match the request.");
            if (response.Error is { } error)
                throw new GatewayRequestException(error);
            return response.Result ?? throw new InvalidResponseException("Gateway response omitted result.");
        }
        finally { _requestLock.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_accessToken is not null)
        {
            try { await SendRequestAsync(RequestType.AuthLogout, new { }, true, CancellationToken.None).ConfigureAwait(false); }
            catch { }
        }
        await _connection.DisposeAsync().ConfigureAwait(false);
        _requestLock.Dispose();
    }
}

public sealed class GatewayRequestException(ProtocolError error) : NodeDeckProtocolException(error.Code, $"{error.Code}: {error.Message}")
{
    public ProtocolError Error { get; } = error;
}
