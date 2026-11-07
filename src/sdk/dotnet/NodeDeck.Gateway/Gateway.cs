using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using NLog;
using NodeDeck.Core.Protocol;
using NodeDeck.Core.Transport;
using NodeDeck.Transport.Tcp;

namespace NodeDeck.Gateway;

public sealed class GatewayBuilder(string gatewayId, string name, string sharedSecret)
{
    private readonly GatewayCommandRegistry _commands = new();
    private GatewaySessionOptions _sessionOptions = new();
    public GatewayBuilder WithSessions(GatewaySessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _sessionOptions = options;
        return this;
    }
    public GatewayBuilder AddCommand(GatewayCommand command)
    {
        _commands.Add(command);
        return this;
    }
    public GatewayServer Build(IProtocolMessageListener listener, string? description = null) =>
        BuildWithListeners([listener ?? throw new ArgumentNullException(nameof(listener))], description);

    public GatewayServer BuildWithListeners(IEnumerable<IProtocolMessageListener> listeners, string? description = null) =>
        new(gatewayId, name, description, sharedSecret, _commands.GetCommands(), listeners, _sessionOptions);
}

/// <summary>Minimal version 0.1 gateway server. TCP is intended for local development; configure a secure tunnel before using real secrets.</summary>
public sealed class GatewayServer : IGateway
{
    private readonly string _gatewayId, _sharedSecret;
    private GatewayInfo _info;
    private readonly Func<ClientInfo, GatewayRoutingSession>? _routingFactory;
    private readonly IProtocolMessageListener[] _listeners;
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly object _sessionsLock = new();
    private readonly GatewaySessionOptions _sessionOptions;
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private Task? _sessionLog;
    private readonly ConcurrentDictionary<string, CachedInvocation> _dedup = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _dedupLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, OperationResult> _operations = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<Task> _connections = [];
    private readonly object _tasksLock = new();
    private Task? _acceptLoop;

    internal GatewayServer(string gatewayId, string name, string? description, string sharedSecret,
        IEnumerable<GatewayCommand> commands, IEnumerable<IProtocolMessageListener> listeners, GatewaySessionOptions sessionOptions,
        Func<ClientInfo, GatewayRoutingSession>? routingFactory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gatewayId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(sharedSecret);
        _sessionOptions = sessionOptions;
        _gatewayId = gatewayId;
        _info = new GatewayInfo(gatewayId, name, description, routingFactory is null ? "executor" : "node");
        _routingFactory = routingFactory;
        _sharedSecret = sharedSecret;
        foreach (var command in commands)
            Commands.Add(command);
        _listeners = listeners.ToArray();
        if (_listeners.Length == 0 || _listeners.Any(listener => listener is null))
            throw new ArgumentException("Provide at least one listener and no null entries.", nameof(listeners));
    }

    public string GatewayId => _gatewayId;
    public GatewayInfo Info => Volatile.Read(ref _info);
    public Task Ready => Task.WhenAll(_listeners.Select(listener => listener.Ready));
    public IReadOnlyList<GatewayConnectionInfo> ConnectedGateways => [];
    public void UpdateMetadata(string name, string? description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Volatile.Write(ref _info, Info with { Name = name, Description = description });
    }
    public GatewayCommandRegistry Commands { get; } = new();
    public int? TcpPort => _listeners.OfType<TcpJsonServer>().FirstOrDefault()?.Port;
    public event Action? ClientLoggedOut;
    public IReadOnlyList<GatewaySessionInfo> ActiveSessions
    {
        get
        {
            lock (_sessionsLock)
                return _sessions.Values.Where(IsActive).Select(s => s.Info).ToArray();
        }
    }

    public bool EndSession(string sessionId)
    {
        lock (_sessionsLock)
        {
            var entry = _sessions.FirstOrDefault(pair => pair.Value.Info.SessionId == sessionId);
            return entry.Key is not null && RemoveSession(entry.Key);
        }
    }

    // Call under _sessionsLock. Revocation also stops the downstream session owned by a routing node.
    private bool RemoveSession(string token)
    {
        if (!_sessions.Remove(token, out var session))
            return false;
        session.Routing?.Revoke();
        return true;
    }

    private bool IsActive(Session session) => session.Info.ExpiresAt > DateTimeOffset.UtcNow &&
        DateTimeOffset.UtcNow - session.Info.LastSeenAt < _sessionOptions.InactivityTimeout;
    public event Action<string, string, JsonElement>? CommandReceived;

    public void Start()
    {
        if (_acceptLoop is not null)
            throw new InvalidOperationException("Gateway has already started.");
        foreach (var listener in _listeners)
            listener.Start();
        _sessionLog = LogActiveUsersAsync(_shutdown.Token);
        _acceptLoop = Task.WhenAll(_listeners.Select(listener => AcceptLoopAsync(listener, _shutdown.Token)));
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (_acceptLoop is null)
            Start();
        await _acceptLoop!.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task AcceptLoopAsync(IProtocolMessageListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var connection = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
                var task = HandleConnectionAsync(connection, cancellationToken);
                lock (_tasksLock)
                    _connections.Add(task);
                _ = task.ContinueWith(t =>
                {
                    lock (_tasksLock)
                        _connections.Remove(t);
                    _ = t.Exception;
                },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                _shutdown.Cancel();
                throw;
            }
        }
    }

    private async Task LogActiveUsersAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(_sessionOptions.ActiveUsersLogInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                var sessions = ActiveSessions;
                Log.Info("Active users ({0}): {1}", sessions.Count, ProtocolJson.SerializeToString(sessions));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task HandleConnectionAsync(IProtocolMessageConnection connection, CancellationToken cancellationToken)
    {
        await using (connection.ConfigureAwait(false))
        using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            var state = new ConnectionState(connection);
            var requests = Channel.CreateBounded<ProtocolRequest>(32);
            var receiver = ReceiveRequestsAsync(state, requests.Writer, lifetime.Token);
            var heartbeat = MonitorSessionAsync(state, lifetime);
            var helloDone = false;
            try
            {
                await foreach (var request in requests.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
                {
                    ProtocolResponse response;
                    try
                    {
                        ProtocolValidator.ValidateRequest(request, helloDone, connection.MaxMessageBytes);
                        request.Route?.ValidateAt(_gatewayId);
                        if (request.Type != RequestType.ProtocolHello && request.Type != RequestType.AuthLogin &&
                            ProtocolConstants.RequestTypes.Contains(request.Type))
                        {
                            lock (_sessionsLock)
                            {
                                if (state.Token is null || request.AccessToken != state.Token ||
                                    !_sessions.TryGetValue(state.Token, out var session) || !IsActive(session))
                                    throw new UnauthorizedException("Session ended. Reconnect to authenticate again.");
                            }
                        }
                        response = await DispatchAsync(request, state, lifetime.Token).ConfigureAwait(false);
                        if (request.Type == RequestType.ProtocolHello && response.Error is null)
                        {
                            helloDone = true;
                            connection.SetMaxMessageBytes(ProtocolLimits.Default.MaxMessageBytes);
                        }
                    }
                    catch (ProtocolValidationException ex)
                    {
                        response = ProtocolResponse.Failure(request, new ProtocolError(ex.Code, ex.Message, false));
                    }
                    catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { throw; }
                    catch (Exception)
                    {
                        response = ProtocolResponse.Failure(request, new ProtocolError(ProtocolErrorCode.InternalError, "The gateway could not process the request.", false));
                    }
                    ProtocolValidator.ValidateResponse(response, connection.MaxMessageBytes);
                    await connection.SendAsync(response, lifetime.Token).ConfigureAwait(false);
                    // Release the transport, including BLE GATT resources, after acknowledging logout.
                    if (request.Type == RequestType.AuthLogout && response.Error is null)
                    {
                        ClientLoggedOut?.Invoke();
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (IOException) { }
            finally
            {
                lifetime.Cancel();
                lock (_sessionsLock)
                    if (state.Token is { } token)
                        RemoveSession(token);
                await Task.WhenAll(receiver, heartbeat).ConfigureAwait(false);
                foreach (var routing in state.Routes)
                    await routing.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task ReceiveRequestsAsync(ConnectionState state, ChannelWriter<ProtocolRequest> writer, CancellationToken token)
    {
        Exception? failure = null;
        try
        {
            while (!token.IsCancellationRequested)
            {
                JsonElement frame;
                try { frame = await state.Connection.ReceiveAsync<JsonElement>(token).ConfigureAwait(false); }
                catch (ProtocolValidationException ex) when (ex is not MessageTooLargeException) { continue; }
                if (frame.ValueKind == JsonValueKind.Undefined)
                    break;
                if (frame.ValueKind != JsonValueKind.Object)
                    continue;
                try
                {
                    if (!frame.TryGetProperty("payload", out _) && frame.TryGetProperty("type", out var type) &&
                        type.ValueKind == JsonValueKind.String && type.GetString() == RequestType.SessionPing)
                    {
                        var pong = frame.Deserialize<ProtocolResponse>(ProtocolJson.Options)!;
                        ProtocolValidator.ValidateResponse(pong, state.Connection.MaxMessageBytes);
                        lock (_sessionsLock)
                        {
                            if (state.Token is { } accessToken && _sessions.TryGetValue(accessToken, out var session) &&
                                IsActive(session) && pong.ProtocolVersion == ProtocolConstants.CurrentVersion &&
                                pong.Type == RequestType.SessionPing && pong.RequestId == session.PendingPing &&
                                pong.Error is null && pong.Result is { ValueKind: JsonValueKind.Object })
                            {
                                session.Info = session.Info with { LastSeenAt = DateTimeOffset.UtcNow };
                                session.PendingPing = null;
                            }
                        }
                        continue;
                    }
                    var request = frame.Deserialize<ProtocolRequest>(ProtocolJson.Options)!;
                    await writer.WriteAsync(request, token).ConfigureAwait(false);
                }
                catch (JsonException) { }
                catch (ProtocolValidationException) { }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { failure = ex; }
        finally
        {
            lock (_sessionsLock)
                if (state.Token is { } accessToken)
                    RemoveSession(accessToken);
            writer.TryComplete(failure);
        }
    }

    private async Task MonitorSessionAsync(ConnectionState state, CancellationTokenSource lifetime)
    {
        using var timer = new PeriodicTimer(_sessionOptions.PingInterval < TimeSpan.FromSeconds(1)
            ? _sessionOptions.PingInterval : TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false))
            {
                ProtocolRequest? ping = null;
                lock (_sessionsLock)
                {
                    if (state.Token is not { } token || !_sessions.TryGetValue(token, out var session))
                        continue;
                    if (!IsActive(session))
                    {
                        RemoveSession(token);
                        continue;
                    }
                    if (DateTimeOffset.UtcNow - session.LastPingAt >= _sessionOptions.PingInterval)
                    {
                        session.PendingPing ??= Guid.NewGuid().ToString("N");
                        session.LastPingAt = DateTimeOffset.UtcNow;
                        ping = new ProtocolRequest(ProtocolConstants.CurrentVersion, session.PendingPing,
                            RequestType.SessionPing, JsonSerializer.SerializeToElement(new { }));
                    }
                }
                if (ping is not null)
                {
                    using var sendTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    sendTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                    await state.Connection.SendAsync(ping, sendTimeout.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception) { lifetime.Cancel(); }
    }

    private async Task<ProtocolResponse> DispatchAsync(ProtocolRequest request, ConnectionState state, CancellationToken cancellationToken)
    {
        if (state.Routing is { } routing && request.Type is RequestType.CapabilitiesGet or RequestType.UiGet or RequestType.CommandInvoke or RequestType.OperationGet)
            return await routing.HandleAsync(request, cancellationToken).ConfigureAwait(false);
        switch (request.Type)
        {
            case RequestType.ProtocolHello:
            {
                var hello = request.Payload.Deserialize<HelloPayload>(ProtocolJson.Options) ?? throw new InvalidRequestException("Invalid hello payload.");
                if (hello.Client is null || string.IsNullOrWhiteSpace(hello.Client.Id) || hello.Client.Id.Length > 128 ||
                    string.IsNullOrWhiteSpace(hello.Client.Name) || hello.Client.Name.Length > 256 || hello.SupportedVersions is null)
                    throw new InvalidRequestException("hello requires a device ID (1-128 characters), name (1-256 characters), and supported versions.");
                state.Client = hello.Client;
                var selected = hello.SupportedVersions.Contains(ProtocolConstants.CurrentVersion, StringComparer.Ordinal);
                return selected
                    ? ProtocolResponse.Success(request, new HelloResult(ProtocolConstants.CurrentVersion, _gatewayId, [AuthMethod.SharedSecret], ProtocolLimits.Default.MaxMessageBytes))
                    : ProtocolResponse.Failure(request, new ProtocolError(ProtocolErrorCode.UnsupportedVersion, "No supported protocol version.", false));
            }
            case RequestType.AuthLogin:
            {
                var login = request.Payload.Deserialize<LoginPayload>(ProtocolJson.Options) ?? throw new InvalidRequestException("Invalid login payload.");
                if (login.Method != AuthMethod.SharedSecret || login.Secret is null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(login.Secret), Encoding.UTF8.GetBytes(_sharedSecret)))
                    throw new UnauthorizedException("Invalid credentials.");
                var accessToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                var expires = DateTimeOffset.UtcNow.AddHours(12);
                lock (_sessionsLock)
                {
                    // Replace the previous device session only after successful authentication.
                    foreach (var previous in _sessions.Where(pair => pair.Value.Info.DeviceId == state.Client!.Id).Select(pair => pair.Key).ToArray())
                        RemoveSession(previous);
                    var now = DateTimeOffset.UtcNow;
                    state.Routing = _routingFactory?.Invoke(state.Client!);
                    if (state.Routing is not null)
                        state.Routes.Add(state.Routing);
                    _sessions[accessToken] = new Session(new GatewaySessionInfo(Guid.NewGuid().ToString("N"), "operator",
                        state.Client!.Id, state.Client.Name, state.Connection.GetType().Name, now, now, expires), state.Routing);
                    state.Token = accessToken;
                }
                return ProtocolResponse.Success(request, new LoginResult(accessToken, expires, "operator"));
            }
            case RequestType.AuthLogout:
                if (request.AccessToken is { } logoutToken)
                    lock (_sessionsLock)
                        RemoveSession(logoutToken);
                return ProtocolResponse.Success(request, new LogoutResult(true));
            case RequestType.CapabilitiesGet:
                return ProtocolResponse.Success(request, GetCapabilities(Commands.GetSnapshot()));
            case RequestType.UiGet:
            {
                var snapshot = Commands.GetSnapshot();
                return ProtocolResponse.Success(request, new UiDocument(snapshot.UiRevision, GetCapabilities(snapshot).Revision, null, []));
            }
            case RequestType.CommandInvoke:
                return await InvokeAsync(request, cancellationToken).ConfigureAwait(false);
            case RequestType.OperationGet:
            {
                var payload = request.Payload.Deserialize<OperationGetPayload>(ProtocolJson.Options);
                if (payload?.OperationToken is null || !_operations.TryGetValue(payload.OperationToken, out var operation))
                    throw new OperationNotFoundException("Operation not found.");
                return ProtocolResponse.Success(request, operation);
            }
            default:
                return ProtocolResponse.Failure(request, new ProtocolError(ProtocolErrorCode.UnsupportedType, "Unknown request type.", false));
        }
    }

    private CapabilitiesDocument GetCapabilities(GatewayCommandRegistry.Snapshot snapshot)
    {
        var definitions = snapshot.Commands.Values.Select(command => command.GetDefinition()).ToArray();
        // Availability is part of the catalog, but changing it does not invalidate the UI schema.
        var info = Info;
        var hash = Convert.ToHexString(SHA256.HashData(ProtocolJson.Serialize(new { info, definitions })));
        return new CapabilitiesDocument(info, $"cap-{snapshot.Revision}-{hash}",
            [ProtocolFeature.Commands, ProtocolFeature.OperationsPoll, ProtocolFeature.Ui], ProtocolLimits.Default,
            definitions, [], []);
    }

    private async Task<ProtocolResponse> InvokeAsync(ProtocolRequest request, CancellationToken cancellationToken)
    {
        var payload = request.Payload.Deserialize<CommandInvokePayload>(ProtocolJson.Options)
            ?? throw new InvalidRequestException($"Invalid {RequestType.CommandInvoke} payload.");
        string deviceId;
        lock (_sessionsLock)
        {
            if (request.AccessToken is null || !_sessions.TryGetValue(request.AccessToken, out var session) || !IsActive(session))
                throw new UnauthorizedException("Session ended. Reconnect to authenticate again.");
            deviceId = session.Info.DeviceId;
        }
        var key = JsonSerializer.Serialize(new[] { "operator", deviceId, request.RequestId });
        var fingerprint = Canonicalize(request.Payload);
        var reservation = _dedupLocks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await reservation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await InvokeReservedAsync(request, payload, key, fingerprint, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            reservation.Release();
        }
    }

    private async Task<ProtocolResponse> InvokeReservedAsync(ProtocolRequest request, CommandInvokePayload payload,
        string key, string fingerprint, CancellationToken cancellationToken)
    {
        if (_dedup.TryGetValue(key, out var cached))
        {
            if (cached.Fingerprint != fingerprint)
                throw new RequestConflictException("requestId was already used with a different payload.");
            return cached.Response;
        }
        ProtocolResponse response;
        var snapshot = Commands.GetSnapshot();
        if (payload.UiRevision is not null && payload.UiRevision != snapshot.UiRevision)
            response = ProtocolResponse.Success(request, new CommandExecutionResult(ExecutionStatus.Rejected, Error: new ProtocolError(ProtocolErrorCode.SchemaOutdated, "UI schema has changed.", false)));
        else if (!snapshot.Commands.TryGetValue(payload.CommandId, out var command))
            response = ProtocolResponse.Success(request, new CommandExecutionResult(ExecutionStatus.Rejected, Error: new ProtocolError(ProtocolErrorCode.CommandNotFound, "Command not found.", false)));
        else if (command.GetAvailability() is { Enabled: false } availability)
            response = ProtocolResponse.Success(request, new CommandExecutionResult(ExecutionStatus.Rejected, Error: new ProtocolError(ProtocolErrorCode.CommandUnavailable, availability.Reason ?? "Command unavailable.", false)));
        else
        {
            var errors = ProtocolValidator.ValidateArguments(command.Parameters, payload.Arguments);
            if (errors.Count != 0)
                response = ProtocolResponse.Success(request, new CommandExecutionResult(ExecutionStatus.Rejected, Error: new ProtocolError(ProtocolErrorCode.InvalidArguments, errors[0], false)));
            else
            {
                try
                {
                    CommandReceived?.Invoke("operator", payload.CommandId, payload.Arguments.Clone());
                    var result = await command.Handler(payload.Arguments, cancellationToken).ConfigureAwait(false);
                    var outputErrors = ProtocolValidator.ValidateArguments(command.Result, result);
                    if (outputErrors.Count != 0)
                        throw new InvalidOperationException("Handler returned an invalid result.");
                    response = ProtocolResponse.Success(request, new CommandExecutionResult(ExecutionStatus.Completed, result));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    response = ProtocolResponse.Success(request, new CommandExecutionResult(ExecutionStatus.Failed, Error: new ProtocolError(ProtocolErrorCode.ExecutionFailed, "Command execution failed.", false)));
                }
            }
        }
        _dedup[key] = new CachedInvocation(fingerprint, response, DateTimeOffset.UtcNow.AddMilliseconds(ProtocolLimits.Default.DeduplicationWindowMs));
        return response;
    }

    internal static string Canonicalize(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", value.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal).Select(x => JsonSerializer.Serialize(x.Name) + ":" + Canonicalize(x.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", value.EnumerateArray().Select(Canonicalize)) + "]",
        JsonValueKind.String => JsonSerializer.Serialize(value.GetString()),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true", JsonValueKind.False => "false", JsonValueKind.Null => "null",
        _ => throw new InvalidRequestException("Unsupported JSON value.")
    };

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        await Task.WhenAll(_listeners.Select(listener => listener.DisposeAsync().AsTask())).ConfigureAwait(false);
        if (_acceptLoop is not null)
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
        Task[] tasks;
        lock (_tasksLock)
            tasks = _connections.ToArray();
        if (tasks.Length > 0)
            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch { }
        if (_sessionLog is not null)
            await _sessionLog.ConfigureAwait(false);
        _shutdown.Dispose();
    }

    private sealed class Session(GatewaySessionInfo info, GatewayRoutingSession? routing)
    {
        public GatewaySessionInfo Info = info;
        public GatewayRoutingSession? Routing { get; } = routing;
        public DateTimeOffset LastPingAt = info.ConnectedAt;
        public string? PendingPing;
    }
    private sealed class ConnectionState(IProtocolMessageConnection connection)
    {
        public IProtocolMessageConnection Connection { get; } = connection;
        public ClientInfo? Client;
        public GatewayRoutingSession? Routing;
        public List<GatewayRoutingSession> Routes { get; } = [];
        public string? Token;
    }
    private sealed record CachedInvocation(string Fingerprint, ProtocolResponse Response, DateTimeOffset ExpiresAt);
}
