using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NodeDeck.Protocol;
using NodeDeck.Transport.Tcp;

namespace NodeDeck.Gateway;

public delegate ValueTask<JsonElement> GatewayCommandHandler(JsonElement arguments, CancellationToken cancellationToken);
public sealed record GatewayCommand(CommandDefinition Definition, GatewayCommandHandler Handler);

public sealed class GatewayBuilder(string gatewayId, string name, string sharedSecret)
{
    private readonly List<GatewayCommand> _commands = [];
    public GatewayBuilder AddCommand(CommandDefinition definition, GatewayCommandHandler handler)
    {
        ArgumentNullException.ThrowIfNull(definition); ArgumentNullException.ThrowIfNull(handler);
        if (_commands.Any(x => x.Definition.Id == definition.Id)) throw new ArgumentException($"Command '{definition.Id}' already exists.");
        _commands.Add(new GatewayCommand(definition, handler)); return this;
    }
    public GatewayServer Build(IPAddress address, int port, string? description = null) =>
        new(gatewayId, name, description, sharedSecret, _commands, new TcpJsonServer(address, port, 262144));

    public GatewayServer Build(IProtocolMessageListener listener, string? description = null) =>
        new(gatewayId, name, description, sharedSecret, _commands, listener);
}

/// <summary>Minimal version 0.1 gateway server. TCP is intended for local development; configure a secure tunnel before using real secrets.</summary>
public sealed class GatewayServer : IAsyncDisposable
{
    private readonly string _gatewayId, _name, _sharedSecret;
    private readonly string? _description;
    private readonly Dictionary<string, GatewayCommand> _commands;
    private readonly IProtocolMessageListener _listener;
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CachedInvocation> _dedup = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _dedupLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, OperationResult> _operations = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<Task> _connections = [];
    private readonly object _tasksLock = new();
    private readonly string _capabilitiesRevision = "cap-1";
    private readonly string _uiRevision = "ui-1";
    private readonly CapabilitiesDocument _capabilities;
    private readonly UiDocument _ui;
    private Task? _acceptLoop;

    internal GatewayServer(string gatewayId, string name, string? description, string sharedSecret,
        IEnumerable<GatewayCommand> commands, IProtocolMessageListener listener)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gatewayId); ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(sharedSecret);
        _gatewayId = gatewayId; _name = name; _description = description; _sharedSecret = sharedSecret;
        _commands = commands.ToDictionary(x => x.Definition.Id, StringComparer.Ordinal);
        _capabilities = new CapabilitiesDocument(new GatewayInfo(gatewayId, name, description), _capabilitiesRevision,
            ["commands", "operations.poll", "ui"], new ProtocolLimits(262144, 1000, 250, 86400000, 86400000, false),
            _commands.Values.Select(x => x.Definition).ToArray(), [], []);
        ProtocolValidator.ValidateCapabilities(_capabilities);
        _ui = new UiDocument(_uiRevision, _capabilitiesRevision, null, []);
        _listener = listener ?? throw new ArgumentNullException(nameof(listener));
    }

    public string GatewayId => _gatewayId;
    public int? TcpPort => (_listener as TcpJsonServer)?.Port;
    public event Action? ClientLoggedOut;
    public event Action<string, string, JsonElement>? CommandReceived;

    public void Start()
    {
        if (_acceptLoop is not null) throw new InvalidOperationException("Gateway has already started.");
        _listener.Start();
        _acceptLoop = AcceptLoopAsync(_shutdown.Token);
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (_acceptLoop is null) Start();
        await _acceptLoop!.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var connection = await _listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
                var task = HandleConnectionAsync(connection, cancellationToken);
                lock (_tasksLock) _connections.Add(task);
                _ = task.ContinueWith(t => { lock (_tasksLock) _connections.Remove(t); _ = t.Exception; },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (SocketException) when (cancellationToken.IsCancellationRequested) { break; }
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
                try { request = await connection.ReceiveAsync<ProtocolRequest>(cancellationToken).ConfigureAwait(false); }
                catch (ProtocolValidationException ex)
                {
                    // Malformed messages without a trustworthy requestId cannot be correlated safely.
                    if (ex.Code == "message_too_large") break;
                    continue;
                }
                if (request is null) break;
                ProtocolResponse response;
                try
                {
                    ProtocolValidator.ValidateRequest(request, helloDone, maxBytes);
                    if (request.Type != "protocol.hello" && ProtocolConstants.RequestTypes.Contains(request.Type))
                    {
                        if (request.Type != "auth.login")
                        {
                            if (!string.Equals(request.AccessToken, token, StringComparison.Ordinal) || token is null ||
                                !_sessions.TryGetValue(token, out var session) || session.ExpiresAt <= DateTimeOffset.UtcNow)
                            {
                                if (token is not null) _sessions.TryRemove(token, out _);
                                throw new ProtocolValidationException("unauthorized", "Authentication required.");
                            }
                        }
                    }
                    response = await DispatchAsync(request, connection, cancellationToken).ConfigureAwait(false);
                    if (request.Type == "protocol.hello" && response.Error is null)
                    {
                        helloDone = true;
                        maxBytes = _capabilities.Limits.MaxMessageBytes;
                        connection.SetMaxMessageBytes(maxBytes);
                    }
                    if (request.Type == "auth.login" && response.Error is null)
                    {
                        using var resultDoc = JsonDocument.Parse(response.Result!.Value.GetRawText());
                        token = resultDoc.RootElement.GetProperty("accessToken").GetString();
                    }
                    if (request.Type == "auth.logout" && response.Error is null)
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
                    response = ProtocolResponse.Failure(request, new ProtocolError("internal_error", "The gateway could not process the request.", false));
                }
                try { ProtocolValidator.ValidateResponse(response, maxBytes); await connection.SendAsync(response, cancellationToken).ConfigureAwait(false); }
                catch (ProtocolValidationException) { break; }
                catch (IOException) { break; }
            }
        }
    }

    private async Task<ProtocolResponse> DispatchAsync(ProtocolRequest request, IProtocolMessageConnection connection, CancellationToken cancellationToken)
    {
        switch (request.Type)
        {
            case "protocol.hello":
            {
                var hello = request.Payload.Deserialize<HelloPayload>(ProtocolJson.Options) ?? throw new ProtocolValidationException("invalid_request", "Invalid hello payload.");
                var selected = hello.SupportedVersions.Contains(ProtocolConstants.CurrentVersion, StringComparer.Ordinal);
                return selected
                    ? ProtocolResponse.Success(request, new HelloResult(ProtocolConstants.CurrentVersion, _gatewayId, ["sharedSecret"], _capabilities.Limits.MaxMessageBytes))
                    : ProtocolResponse.Failure(request, new ProtocolError("unsupported_version", "No supported protocol version.", false));
            }
            case "auth.login":
            {
                var login = request.Payload.Deserialize<LoginPayload>(ProtocolJson.Options) ?? throw new ProtocolValidationException("invalid_request", "Invalid login payload.");
                if (login.Method != "sharedSecret" || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(login.Secret), Encoding.UTF8.GetBytes(_sharedSecret)))
                    throw new ProtocolValidationException("unauthorized", "Invalid credentials.");
                var accessToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                var expires = DateTimeOffset.UtcNow.AddHours(12);
                _sessions[accessToken] = new Session("operator", expires);
                return ProtocolResponse.Success(request, new LoginResult(accessToken, expires, "operator"));
            }
            case "auth.logout":
                if (request.AccessToken is { } logoutToken) _sessions.TryRemove(logoutToken, out _);
                return ProtocolResponse.Success(request, new LogoutResult(true));
            case "capabilities.get": return ProtocolResponse.Success(request, _capabilities);
            case "ui.get": return ProtocolResponse.Success(request, _ui);
            case "command.invoke": return await InvokeAsync(request, cancellationToken).ConfigureAwait(false);
            case "operation.get":
            {
                var payload = request.Payload.Deserialize<Dictionary<string, string>>(ProtocolJson.Options);
                if (payload is null || !payload.TryGetValue("operationToken", out var opToken) || !_operations.TryGetValue(opToken, out var operation))
                    throw new ProtocolValidationException("operation_not_found", "Operation not found.");
                return ProtocolResponse.Success(request, operation);
            }
            default: return ProtocolResponse.Failure(request, new ProtocolError("unsupported_type", "Unknown request type.", false));
        }
    }

    private async Task<ProtocolResponse> InvokeAsync(ProtocolRequest request, CancellationToken cancellationToken)
    {
        var payload = request.Payload.Deserialize<CommandInvokePayload>(ProtocolJson.Options)
            ?? throw new ProtocolValidationException("invalid_request", "Invalid command.invoke payload.");
        var key = $"operator\u001f{request.RequestId}";
        var fingerprint = Canonicalize(request.Payload);
        var reservation = _dedupLocks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await reservation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await InvokeReservedAsync(request, payload, key, fingerprint, cancellationToken).ConfigureAwait(false); }
        finally { reservation.Release(); }
    }

    private async Task<ProtocolResponse> InvokeReservedAsync(ProtocolRequest request, CommandInvokePayload payload,
        string key, string fingerprint, CancellationToken cancellationToken)
    {
        if (_dedup.TryGetValue(key, out var cached))
        {
            if (cached.Fingerprint != fingerprint) throw new ProtocolValidationException("request_conflict", "requestId was already used with a different payload.");
            return cached.Response;
        }
        ProtocolResponse response;
        if (payload.UiRevision is not null && payload.UiRevision != _uiRevision)
            response = ProtocolResponse.Success(request, new CommandExecutionResult("rejected", Error: new ProtocolError("schema_outdated", "UI schema has changed.", false)));
        else if (!_commands.TryGetValue(payload.CommandId, out var command))
            response = ProtocolResponse.Success(request, new CommandExecutionResult("rejected", Error: new ProtocolError("command_not_found", "Command not found.", false)));
        else if (!command.Definition.Availability.Enabled)
            response = ProtocolResponse.Success(request, new CommandExecutionResult("rejected", Error: new ProtocolError("command_unavailable", command.Definition.Availability.Reason ?? "Command unavailable.", false)));
        else
        {
            var errors = ProtocolValidator.ValidateArguments(command.Definition.Parameters, payload.Arguments);
            if (errors.Count != 0)
                response = ProtocolResponse.Success(request, new CommandExecutionResult("rejected", Error: new ProtocolError("invalid_arguments", errors[0], false)));
            else
            {
                try
                {
                    CommandReceived?.Invoke("operator", payload.CommandId, payload.Arguments.Clone());
                    var result = await command.Handler(payload.Arguments, cancellationToken).ConfigureAwait(false);
                    var outputErrors = ProtocolValidator.ValidateArguments(command.Definition.Result, result);
                    if (outputErrors.Count != 0) throw new InvalidOperationException("Handler returned an invalid result.");
                    response = ProtocolResponse.Success(request, new CommandExecutionResult("completed", result));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception)
                {
                    response = ProtocolResponse.Success(request, new CommandExecutionResult("failed", Error: new ProtocolError("execution_failed", "Command execution failed.", false)));
                }
            }
        }
        _dedup[key] = new CachedInvocation(fingerprint, response, DateTimeOffset.UtcNow.AddMilliseconds(_capabilities.Limits.DeduplicationWindowMs));
        return response;
    }

    private static string Canonicalize(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", value.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal).Select(x => JsonSerializer.Serialize(x.Name) + ":" + Canonicalize(x.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", value.EnumerateArray().Select(Canonicalize)) + "]",
        JsonValueKind.String => JsonSerializer.Serialize(value.GetString()),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true", JsonValueKind.False => "false", JsonValueKind.Null => "null",
        _ => throw new ProtocolValidationException("invalid_request", "Unsupported JSON value.")
    };

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        await _listener.DisposeAsync().ConfigureAwait(false);
        if (_acceptLoop is not null) try { await _acceptLoop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        Task[] tasks; lock (_tasksLock) tasks = _connections.ToArray();
        if (tasks.Length > 0) try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch { }
        _shutdown.Dispose();
    }

    private sealed record Session(string SubjectId, DateTimeOffset ExpiresAt);
    private sealed record CachedInvocation(string Fingerprint, ProtocolResponse Response, DateTimeOffset ExpiresAt);
}
