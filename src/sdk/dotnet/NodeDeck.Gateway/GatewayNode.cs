using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NodeDeck.Client;
using NodeDeck.Core.Protocol;
using NodeDeck.Core.Transport;
using NLog;
using ProtocolValueType = NodeDeck.Core.Protocol.ValueType;

namespace NodeDeck.Gateway;

/// <summary>Authenticated gateway directory with an independent selected route for every device session.</summary>
public sealed class GatewayNode : IGateway
{
    private readonly GatewayServer _host;
    private readonly GatewaySettings _settings;
    private readonly Dictionary<string, Peer> _peers;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource _initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _refresh;
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private static readonly ValueDescriptor EmptyObject = new()
    {
        Type = ProtocolValueType.Object, Properties = new Dictionary<string, ValueDescriptor>(), Required = [], AdditionalProperties = false
    };
    private static readonly ValueDescriptor SelectionResult = new()
    {
        Type = ProtocolValueType.Object,
        Properties = new Dictionary<string, ValueDescriptor>
        {
            ["gatewayId"] = new() { Type = ProtocolValueType.String },
            ["connected"] = new() { Type = ProtocolValueType.Boolean }
        },
        Required = ["gatewayId", "connected"], AdditionalProperties = false
    };

    internal GatewayNode(GatewaySettings settings, IEnumerable<IProtocolMessageListener> listeners)
    {
        _settings = settings;
        _peers = settings.Gateways.ToDictionary(peer => ConnectCommandId(settings.GatewayId, peer.GatewayId), peer => new Peer(peer));
        _host = new GatewayServer(settings.GatewayId, settings.Name, settings.Description, settings.SharedSecret,
            [], listeners, settings.Sessions, client => new NodeSession(this, client));
    }

    public GatewayInfo Info => _host.Info;
    public Task Ready => Task.WhenAll(_host.Ready, _initialized.Task);
    public IReadOnlyList<GatewaySessionInfo> ActiveSessions => _host.ActiveSessions;
    public IReadOnlyList<GatewayConnectionInfo> ConnectedGateways => _peers.Values.Select(peer => peer.Snapshot).Where(peer => peer.IsConnected).ToArray();
    public void UpdateMetadata(string name, string? description) => _host.UpdateMetadata(name, description);
    public bool EndSession(string sessionId) => _host.EndSession(sessionId);

    public static string ConnectCommandId(string nodeId, string gatewayId) => $"gateway.connect.{Encode(nodeId)}.{Encode(gatewayId)}";
    public static string DisconnectCommandId(string nodeId) => $"gateway.disconnect.{Encode(nodeId)}";
    private static string Encode(string id) => Convert.ToHexString(Encoding.UTF8.GetBytes(id));

    public void Start()
    {
        _host.Start();
        _refresh = RefreshAsync();
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (_refresh is null)
            Start();
        await _host.RunAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RefreshAsync()
    {
        using var timer = new PeriodicTimer(_settings.CatalogRefreshInterval);
        try
        {
            do
            {
                await Task.WhenAll(_peers.Values.Select(RefreshPeerAsync)).ConfigureAwait(false);
                _initialized.TrySetResult();
            } while (await timer.WaitForNextTickAsync(_shutdown.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        finally { _initialized.TrySetResult(); }
    }

    private async Task RefreshPeerAsync(Peer peer)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        timeout.CancelAfter(_settings.RouteTimeout);
        try
        {
            if (peer.Monitor is null || peer.Monitor.Disconnected.IsCompleted)
            {
                if (peer.Monitor is not null)
                    await peer.Monitor.DisposeAsync().ConfigureAwait(false);
                // A catalog connection is not a phone session and must never evict one.
                peer.Monitor = await ConnectAsync(peer, new ClientInfo(Guid.NewGuid().ToString("N"), $"Gateway directory: {Info.Name}"),
                    GatewayRoute.Empty.Forward(Info.Id, peer.Settings.GatewayId), timeout.Token).ConfigureAwait(false);
            }
            var catalog = ReadCatalog(await peer.Monitor.GetCapabilitiesAsync(timeout.Token).ConfigureAwait(false));
            peer.Update(new GatewayConnectionInfo(catalog.Gateway, true, DateTimeOffset.UtcNow));
        }
        catch (Exception ex)
        {
            peer.Update(peer.Snapshot with { IsConnected = false });
            if (peer.Monitor is not null)
                await peer.Monitor.DisposeAsync().ConfigureAwait(false);
            peer.Monitor = null;
            if (!_shutdown.IsCancellationRequested)
                Log.Debug("Gateway {0} is unavailable ({1}).", peer.Settings.GatewayId, ex.GetType().Name);
        }
    }

    private static Task<NodeDeckClientConnection> ConnectAsync(Peer peer, ClientInfo client, GatewayRoute route, CancellationToken token) =>
        NodeDeckClientConnection.ConnectAsync(peer.Settings.Host, peer.Settings.Port, peer.Settings.SharedSecret,
            client.Id, client.Name, token, peer.Settings.GatewayId, route);

    private static CapabilitiesDocument ReadCatalog(JsonElement result)
    {
        var catalog = result.Deserialize<CapabilitiesDocument>(ProtocolJson.Options)
            ?? throw new InvalidResponseException("Gateway returned an invalid catalog.");
        ProtocolValidator.ValidateCapabilities(catalog);
        return catalog;
    }

    private CapabilitiesDocument Directory(GatewayRoute route)
    {
        var commands = _peers.Select(pair =>
        {
            var peer = pair.Value.Snapshot;
            var backwards = route.VisitedGatewayIds.Contains(peer.Gateway.Id, StringComparer.Ordinal);
            return new CommandDefinition(pair.Key, peer.Gateway.Name, peer.Gateway.Description, EmptyObject, SelectionResult,
                new CommandAvailability(peer.IsConnected && !backwards && route.RemainingHops > 0,
                    backwards ? "This gateway is already on the route." : route.RemainingHops == 0 ? "Route hop limit reached." :
                    peer.IsConnected ? null : "Gateway unavailable."));
        }).ToArray();
        var info = Info;
        var revision = Revision(new { info, commands });
        return new CapabilitiesDocument(info, revision, [ProtocolFeature.Commands, ProtocolFeature.Ui], ProtocolLimits.Default, commands, [], []);
    }

    private CommandDefinition BackCommand() => new(DisconnectCommandId(Info.Id), Info.Name,
        "Return to this gateway's directory.", EmptyObject, SelectionResult, new CommandAvailability(true));

    private static string Revision(object value) => "node-" + Convert.ToHexString(SHA256.HashData(ProtocolJson.Serialize(value)));

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        await _host.DisposeAsync().ConfigureAwait(false);
        if (_refresh is not null)
            await _refresh.ConfigureAwait(false);
        foreach (var peer in _peers.Values)
            if (peer.Monitor is not null)
                await peer.Monitor.DisposeAsync().ConfigureAwait(false);
        _shutdown.Dispose();
    }

    private sealed class Peer(GatewayEndpointSettings settings)
    {
        private GatewayConnectionInfo _snapshot = new(new GatewayInfo(settings.GatewayId, settings.Name, settings.Description), false, null);
        public GatewayEndpointSettings Settings { get; } = settings;
        public NodeDeckClientConnection? Monitor;
        public GatewayConnectionInfo Snapshot
        {
            get
            {
                var snapshot = Volatile.Read(ref _snapshot);
                return Monitor?.Disconnected.IsCompleted == true ? snapshot with { IsConnected = false } : snapshot;
            }
        }
        public void Update(GatewayConnectionInfo value) => Volatile.Write(ref _snapshot, value);
    }

    private sealed class NodeSession(GatewayNode node, ClientInfo client) : GatewayRoutingSession
    {
        private readonly CancellationTokenSource _lifetime = new();
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly object _disposeLock = new();
        private readonly Dictionary<string, (string Fingerprint, ProtocolResponse Response)> _localResults = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (string Destination, string Fingerprint)> _invocations = new(StringComparer.Ordinal);
        private Selection? _selected;
        private Task? _cleanup;

        public override async Task<ProtocolResponse> HandleAsync(ProtocolRequest request, CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
            timeout.CancelAfter(node._settings.RouteTimeout);
            await _gate.WaitAsync(timeout.Token).ConfigureAwait(false);
            try
            {
                var route = request.Route ?? GatewayRoute.Empty;
                if (request.Type == RequestType.CommandInvoke)
                {
                    var payload = request.Payload.Deserialize<CommandInvokePayload>(ProtocolJson.Options)
                        ?? throw new InvalidRequestException("Invalid command payload.");
                    var local = node._peers.ContainsKey(payload.CommandId) || payload.CommandId == DisconnectCommandId(node.Info.Id);
                    var invocation = (local ? node.Info.Id : _selected?.Peer.Settings.GatewayId ?? node.Info.Id,
                        GatewayServer.Canonicalize(request.Payload));
                    if (_invocations.TryGetValue(request.RequestId, out var previous) && previous != invocation)
                        throw new RequestConflictException("This requestId was already used for another command or destination.");
                    _invocations[request.RequestId] = invocation;
                    if (local)
                        return await SelectAsync(request, payload, route, timeout.Token).ConfigureAwait(false);
                    if (_selected is null)
                        return Rejected(request, ProtocolErrorCode.CommandNotFound, "Select a gateway before invoking its commands.");
                }
                if (_selected is null)
                {
                    var catalog = node.Directory(route);
                    return request.Type switch
                    {
                        RequestType.CapabilitiesGet => ProtocolResponse.Success(request, catalog),
                        RequestType.UiGet => ProtocolResponse.Success(request, new UiDocument(catalog.Revision, catalog.Revision, null, [])),
                        _ => ProtocolResponse.Failure(request, new ProtocolError(ProtocolErrorCode.OperationNotFound, "No gateway is selected.", false))
                    };
                }

                var destination = _selected.Peer.Settings.GatewayId;
                var forwardedRoute = route.Forward(node.Info.Id, destination);
                if (_selected.Failed || _selected.Connection.Disconnected.IsCompleted)
                    return Unavailable(request);
                try
                {
                    var result = await _selected.Connection.ForwardAsync(request, forwardedRoute, timeout.Token).ConfigureAwait(false);
                    if (request.Type == RequestType.CapabilitiesGet)
                    {
                        _selected.Catalog = ReadCatalog(result);
                        return ProtocolResponse.Success(request, WithBack(_selected.Catalog));
                    }
                    // The downstream schema remains authoritative for uiRevision on invocations.
                    if (request.Type == RequestType.UiGet)
                    {
                        var ui = result.Deserialize<UiDocument>(ProtocolJson.Options)!;
                        return ProtocolResponse.Success(request, ui with { CapabilitiesRevision = WithBack(_selected.Catalog).Revision });
                    }
                    return ProtocolResponse.Success(request, result);
                }
                catch (GatewayRequestException ex)
                {
                    return ProtocolResponse.Failure(request, ex.Error);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or NodeDeckProtocolException)
                {
                    _selected.Failed = true;
                    return Unavailable(request);
                }
            }
            finally { _gate.Release(); }
        }

        private async Task<ProtocolResponse> SelectAsync(ProtocolRequest request, CommandInvokePayload payload, GatewayRoute route, CancellationToken token)
        {
            var fingerprint = GatewayServer.Canonicalize(request.Payload);
            if (_localResults.TryGetValue(request.RequestId, out var cached))
            {
                if (cached.Fingerprint != fingerprint)
                    throw new RequestConflictException("This requestId was already used with another selection.");
                return cached.Response;
            }
            var errors = ProtocolValidator.ValidateArguments(EmptyObject, payload.Arguments);
            if (errors.Count != 0)
                return Rejected(request, ProtocolErrorCode.InvalidArguments, errors[0]);
            var connected = payload.CommandId != DisconnectCommandId(node.Info.Id);
            var gatewayId = node.Info.Id;
            if (connected)
            {
                var peer = node._peers[payload.CommandId];
                gatewayId = peer.Settings.GatewayId;
                var nextRoute = route.Forward(node.Info.Id, gatewayId);
                NodeDeckClientConnection? candidate = null;
                try
                {
                    candidate = await ConnectAsync(peer, client, nextRoute, token).ConfigureAwait(false);
                    var catalog = ReadCatalog(await candidate.GetCapabilitiesAsync(token).ConfigureAwait(false));
                    if (_selected is not null)
                        await _selected.Connection.DisposeAsync().ConfigureAwait(false);
                    _selected = new Selection(peer, candidate, catalog);
                }
                catch (GatewayRequestException ex)
                {
                    if (candidate is not null)
                        await candidate.DisposeAsync().ConfigureAwait(false);
                    return ProtocolResponse.Failure(request, ex.Error);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or NodeDeckProtocolException)
                {
                    if (candidate is not null)
                        await candidate.DisposeAsync().ConfigureAwait(false);
                    return Rejected(request, ProtocolErrorCode.GatewayUnavailable, "The selected gateway is unavailable.");
                }
            }
            else
            {
                if (_selected is not null)
                    await _selected.Connection.DisposeAsync().ConfigureAwait(false);
                _selected = null;
            }
            var response = ProtocolResponse.Success(request, new CommandExecutionResult(ExecutionStatus.Completed,
                JsonSerializer.SerializeToElement(new { gatewayId, connected }), RefreshCapabilities: true));
            _localResults[request.RequestId] = (fingerprint, response);
            return response;
        }

        private CapabilitiesDocument WithBack(CapabilitiesDocument catalog, bool available = true)
        {
            var commands = catalog.Commands.Select(command => available ? command : command with
            {
                Availability = new CommandAvailability(false, "Gateway unavailable. Return to the directory and reconnect.")
            }).Append(node.BackCommand()).ToArray();
            return catalog with { Commands = commands, Revision = Revision(new { catalog.Revision, commands }) };
        }

        private ProtocolResponse Unavailable(ProtocolRequest request) => request.Type == RequestType.CapabilitiesGet
            ? ProtocolResponse.Success(request, WithBack(_selected!.Catalog, available: false))
            : request.Type == RequestType.CommandInvoke
                ? ProtocolResponse.Success(request, new CommandExecutionResult(ExecutionStatus.Failed,
                    Error: new ProtocolError(ProtocolErrorCode.GatewayUnavailable, "The selected gateway is unavailable; the command was not replayed. Its outcome may be unknown.", false),
                    RefreshCapabilities: true))
                : ProtocolResponse.Failure(request, new ProtocolError(ProtocolErrorCode.GatewayUnavailable, "The selected gateway is unavailable.", false));

        private static ProtocolResponse Rejected(ProtocolRequest request, string code, string message) =>
            ProtocolResponse.Success(request, new CommandExecutionResult(ExecutionStatus.Rejected, Error: new ProtocolError(code, message, false)));

        public override void Revoke()
        {
            lock (_disposeLock)
            {
                if (_cleanup is not null)
                    return;
                _lifetime.Cancel();
                _cleanup = CleanupAsync();
            }
        }

        private async Task CleanupAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_selected is not null)
                    await _selected.Connection.DisposeAsync().ConfigureAwait(false);
                _selected = null;
            }
            finally { _gate.Release(); }
        }

        public override ValueTask DisposeAsync()
        {
            Revoke();
            return new ValueTask(_cleanup!);
        }

        private sealed class Selection(Peer peer, NodeDeckClientConnection connection, CapabilitiesDocument catalog)
        {
            public Peer Peer { get; } = peer;
            public NodeDeckClientConnection Connection { get; } = connection;
            public CapabilitiesDocument Catalog = catalog;
            public bool Failed;
        }
    }
}
