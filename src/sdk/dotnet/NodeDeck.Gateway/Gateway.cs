using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NodeDeck.Core.Protocol;
using NodeDeck.Core.Transport;
using NodeDeck.Transport.Tcp;

namespace NodeDeck.Gateway;

public sealed class GatewayBuilder(string gatewayId, string name, string sharedSecret)
{
    private readonly GatewayCommandRegistry _commands = new();
    public GatewayBuilder AddCommand(GatewayCommand command)
    {
        _commands.Add(command);
        return this;
    }
    public GatewayServer Build(IProtocolMessageListener listener, string? description = null) =>
        BuildWithListeners([listener ?? throw new ArgumentNullException(nameof(listener))], description);

    public GatewayServer BuildWithListeners(IEnumerable<IProtocolMessageListener> listeners, string? description = null) =>
        new(gatewayId, name, description, sharedSecret, _commands.GetCommands(), listeners);
}

/// <summary>Minimal version 0.1 gateway server. TCP is intended for local development; configure a secure tunnel before using real secrets.</summary>
public sealed class GatewayServer : IAsyncDisposable
{
    private readonly string _gatewayId, _name, _sharedSecret;
    private readonly string? _description;
    private readonly IProtocolMessageListener[] _listeners;
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CachedInvocation> _dedup = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _dedupLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, OperationResult> _operations = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<Task> _connections = [];
    private readonly object _tasksLock = new();
    private Task? _acceptLoop;

    internal GatewayServer(string gatewayId, string name, string? description, string sharedSecret,
        IEnumerable<GatewayCommand> commands, IEnumerable<IProtocolMessageListener> listeners)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gatewayId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(sharedSecret);
        _gatewayId = gatewayId;
        _name = name;
        _description = description;
        _sharedSecret = sharedSecret;
        foreach (var command in commands)
            Commands.Add(command);
        _listeners = listeners.ToArray();
        if (_listeners.Length == 0 || _listeners.Any(listener => listener is null))
            throw new ArgumentException("Provide at least one listener and no null entries.", nameof(listeners));
    }

    public string GatewayId => _gatewayId;
    public GatewayCommandRegistry Commands { get; } = new();
    public int? TcpPort => _listeners.OfType<TcpJsonServer>().FirstOrDefault()?.Port;
    public event Action? ClientLoggedOut;
    public event Action<string, string, JsonElement>? CommandReceived;

    public void Start()
    {
        if (_acceptLoop is not null)
            throw new InvalidOperationException("Gateway has already started.");
        foreach (var listener in _listeners)
            listener.Start();
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

    private async Task HandleConnectionAsync(IProtocolMessageConnection connection, CancellationToken cancellationToken)
    {
        await using (connection.ConfigureAwait(false))
        {
            string? token = null;
            var helloDone = false;
            var maxBytes = ProtocolConstants.InitialMaxMessageBytes;
            while (!cancellationToken.IsCancellationRequested)
            {
                ProtocolRequest? request;
                try
                {
                    request = await connection.ReceiveAsync<ProtocolRequest>(cancellationToken).ConfigureAwait(false);
                }
                catch (ProtocolValidationException ex)
                {
                    // Malformed messages without a trustworthy requestId cannot be correlated safely.
                    if (ex is MessageTooLargeException)
                        break;
                    continue;
                }
                if (request is null)
                    break;
                ProtocolResponse response;
                try
                {
                    ProtocolValidator.ValidateRequest(request, helloDone, maxBytes);
                    if (request.Type != RequestType.ProtocolHello && ProtocolConstants.RequestTypes.Contains(request.Type))
                    {
                        if (request.Type != RequestType.AuthLogin)
                        {
                            if (!string.Equals(request.AccessToken, token, StringComparison.Ordinal) || token is null ||
                                !_sessions.TryGetValue(token, out var session) || session.ExpiresAt <= DateTimeOffset.UtcNow)
                            {
                                if (token is not null)
                                    _sessions.TryRemove(token, out _);
                                throw new UnauthorizedException("Authentication required.");
                            }
                        }
                    }
                    response = await DispatchAsync(request, connection, cancellationToken).ConfigureAwait(false);
                    if (request.Type == RequestType.ProtocolHello && response.Error is null)
                    {
                        helloDone = true;
                        maxBytes = ProtocolLimits.Default.MaxMessageBytes;
                        connection.SetMaxMessageBytes(maxBytes);
                    }
                    if (request.Type == RequestType.AuthLogin && response.Error is null)
                    {
                        token = response.Result!.Value.Deserialize<LoginResult>(ProtocolJson.Options)!.AccessToken;
                    }
                    if (request.Type == RequestType.AuthLogout && response.Error is null)
                    {
                        token = null;
                        ClientLoggedOut?.Invoke();
                    }
                }
                catch (ProtocolValidationException ex)
                {
                    response = ProtocolResponse.Failure(request, new ProtocolError(ex.Code, ex.Message, false));
                }
                catch (Exception)
                {
                    response = ProtocolResponse.Failure(request, new ProtocolError(ProtocolErrorCode.InternalError, "The gateway could not process the request.", false));
                }
                try
                {
                    ProtocolValidator.ValidateResponse(response, maxBytes);
                    await connection.SendAsync(response, cancellationToken).ConfigureAwait(false);
                }
                catch (ProtocolValidationException)
                {
                    break;
                }
                catch (IOException)
                {
                    break;
                }
            }
        }
    }

    private async Task<ProtocolResponse> DispatchAsync(ProtocolRequest request, IProtocolMessageConnection connection, CancellationToken cancellationToken)
    {
        switch (request.Type)
        {
            case RequestType.ProtocolHello:
            {
                var hello = request.Payload.Deserialize<HelloPayload>(ProtocolJson.Options) ?? throw new InvalidRequestException("Invalid hello payload.");
                var selected = hello.SupportedVersions.Contains(ProtocolConstants.CurrentVersion, StringComparer.Ordinal);
                return selected
                    ? ProtocolResponse.Success(request, new HelloResult(ProtocolConstants.CurrentVersion, _gatewayId, [AuthMethod.SharedSecret], ProtocolLimits.Default.MaxMessageBytes))
                    : ProtocolResponse.Failure(request, new ProtocolError(ProtocolErrorCode.UnsupportedVersion, "No supported protocol version.", false));
            }
            case RequestType.AuthLogin:
            {
                var login = request.Payload.Deserialize<LoginPayload>(ProtocolJson.Options) ?? throw new InvalidRequestException("Invalid login payload.");
                if (login.Method != AuthMethod.SharedSecret || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(login.Secret), Encoding.UTF8.GetBytes(_sharedSecret)))
                    throw new UnauthorizedException("Invalid credentials.");
                var accessToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                var expires = DateTimeOffset.UtcNow.AddHours(12);
                _sessions[accessToken] = new Session("operator", expires);
                return ProtocolResponse.Success(request, new LoginResult(accessToken, expires, "operator"));
            }
            case RequestType.AuthLogout:
                if (request.AccessToken is { } logoutToken)
                    _sessions.TryRemove(logoutToken, out _);
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
        var hash = Convert.ToHexString(SHA256.HashData(ProtocolJson.Serialize(definitions)));
        return new CapabilitiesDocument(new GatewayInfo(_gatewayId, _name, _description), $"cap-{snapshot.Revision}-{hash}",
            [ProtocolFeature.Commands, ProtocolFeature.OperationsPoll, ProtocolFeature.Ui], ProtocolLimits.Default,
            definitions, [], []);
    }

    private async Task<ProtocolResponse> InvokeAsync(ProtocolRequest request, CancellationToken cancellationToken)
    {
        var payload = request.Payload.Deserialize<CommandInvokePayload>(ProtocolJson.Options)
            ?? throw new InvalidRequestException($"Invalid {RequestType.CommandInvoke} payload.");
        var key = $"operator\u001f{request.RequestId}";
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

    private static string Canonicalize(JsonElement value) => value.ValueKind switch
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
        _shutdown.Dispose();
    }

    private sealed record Session(string SubjectId, DateTimeOffset ExpiresAt);
    private sealed record CachedInvocation(string Fingerprint, ProtocolResponse Response, DateTimeOffset ExpiresAt);
}
