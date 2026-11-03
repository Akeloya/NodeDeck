using System.Text.Json;
using System.Text.Json.Serialization;

namespace NodeDeck.Protocol;

public static class ProtocolConstants
{
    public const string CurrentVersion = "0.1";
    public const int InitialMaxMessageBytes = 16_384;
    public const long MaxSafeInteger = 9_007_199_254_740_991;
    public static readonly IReadOnlySet<string> RequestTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "protocol.hello", "auth.login", "auth.logout", "capabilities.get", "ui.get", "command.invoke", "operation.get"
    };
}

public static class BleProtocolConstants
{
    public const string ServiceUuid = "a1b2c3d4-e5f6-4a1b-8c9d-001122334455";
    public const string ClientToGatewayCharacteristicUuid = "a1b2c3d4-e5f6-4a1b-8c9d-001122334456";
    public const string GatewayToClientCharacteristicUuid = "a1b2c3d4-e5f6-4a1b-8c9d-001122334457";
}

public sealed record ProtocolError(
    string Code,
    string Message,
    bool Retryable,
    JsonElement? Details = null,
    long? RetryAfterMs = null);

public sealed record ProtocolRequest(
    string ProtocolVersion,
    string RequestId,
    string Type,
    JsonElement Payload,
    string? AccessToken = null);

public interface IProtocolMessageConnection : IAsyncDisposable
{
    int MaxMessageBytes { get; }
    void SetMaxMessageBytes(int maxMessageBytes);
    Task SendAsync<T>(T message, CancellationToken cancellationToken = default);
    Task<T?> ReceiveAsync<T>(CancellationToken cancellationToken = default);
}

public interface IProtocolMessageListener : IAsyncDisposable
{
    void Start();
    Task<IProtocolMessageConnection> AcceptAsync(CancellationToken cancellationToken = default);
}

public sealed record ProtocolResponse(
    string ProtocolVersion,
    string RequestId,
    string Type,
    JsonElement? Result = null,
    ProtocolError? Error = null)
{
    public static ProtocolResponse Success(ProtocolRequest request, object result) =>
        new(request.ProtocolVersion, request.RequestId, request.Type, JsonSerializer.SerializeToElement(result, ProtocolJson.Options));

    public static ProtocolResponse Failure(ProtocolRequest request, ProtocolError error) =>
        new(request.ProtocolVersion, request.RequestId, request.Type, Error: error);
}

public sealed record ClientInfo(string Id, string Name);
public sealed record HelloPayload(IReadOnlyList<string> SupportedVersions, ClientInfo Client);
public sealed record HelloResult(string SelectedVersion, string GatewayId, IReadOnlyList<string> AuthMethods, int MaxMessageBytes);
public sealed record LoginPayload(string Method, string Secret);
public sealed record LoginResult(string AccessToken, DateTimeOffset ExpiresAt, string SubjectId);
public sealed record LogoutResult(bool Revoked);
public sealed record CommandInvokePayload(string CommandId, JsonElement Arguments, string? UiRevision = null);
public sealed record AcceptedCommandResult(string Status, string OperationToken, int PollAfterMs);
public sealed record CommandExecutionResult(string Status, JsonElement? Output = null, ProtocolError? Error = null,
    string? OperationToken = null, int? PollAfterMs = null);
public sealed record OperationProgress(double Value, string? Message = null);
public sealed record OperationResult(string OperationToken, string Status, JsonElement? Output = null,
    ProtocolError? Error = null, OperationProgress? Progress = null, int? PollAfterMs = null);

public enum ValueType { Object, Array, String, Integer, Number, Boolean }

public sealed record ValueDescriptor
{
    public required ValueType Type { get; init; }
    public string? Description { get; init; }
    public IReadOnlyDictionary<string, ValueDescriptor>? Properties { get; init; }
    public IReadOnlyList<string>? Required { get; init; }
    public bool? AdditionalProperties { get; init; }
    public ValueDescriptor? Items { get; init; }
    public int? MinItems { get; init; }
    public int? MaxItems { get; init; }
    public int? MinLength { get; init; }
    public int? MaxLength { get; init; }
    public IReadOnlyList<JsonElement>? Enum { get; init; }
    public double? Minimum { get; init; }
    public double? Maximum { get; init; }
}

public sealed record CommandAvailability(bool Enabled, string? Reason = null);
public sealed record CommandDefinition(string Id, string Title, string? Description, ValueDescriptor Parameters,
    ValueDescriptor Result, CommandAvailability Availability);
public sealed record GatewayInfo(string Id, string Name, string? Description = null);
public sealed record ProtocolLimits(int MaxMessageBytes, int CommandResponseTimeoutMs, int MinPollIntervalMs,
    long OperationRetentionMs, long DeduplicationWindowMs, bool DeduplicationPersistent);
public sealed record ResourceAuth(string Mode);
public sealed record ResourceDefinition(string Id, string MediaType, string Uri, ResourceAuth Auth, int? Width = null, int? Height = null);
public sealed record EndpointDefinition(string Id, string Kind, string Title, string Uri, string Protocol,
    string? Contract, ResourceAuth Auth);
public sealed record CapabilitiesDocument(GatewayInfo Gateway, string Revision, IReadOnlyList<string> Features,
    ProtocolLimits Limits, IReadOnlyList<CommandDefinition> Commands,
    IReadOnlyList<ResourceDefinition> Resources, IReadOnlyList<EndpointDefinition> Endpoints);

public sealed record UiDocument(string Revision, string CapabilitiesRevision, string? StartScreenId, IReadOnlyList<ScreenDefinition> Screens);
public sealed record ScreenDefinition(string Id, string Title, string? Description, ScreenPresentation Presentation,
    UiNode Root, UiCommand? EnterCommand = null);
public sealed record ScreenPresentation(string Kind, string? Side = null, double? Width = null);
public sealed record UiNode
{
    public required string Id { get; init; }
    public required string Type { get; init; }
    public string? Label { get; init; }
    public string? Description { get; init; }
    public bool? Visible { get; init; }
    public bool? Enabled { get; init; }
    public string? Text { get; init; }
    public string? Placeholder { get; init; }
    public string? Alt { get; init; }
    public string? ResourceId { get; init; }
    public JsonElement? InitialValue { get; init; }
    public double? Minimum { get; init; }
    public double? Maximum { get; init; }
    public double? Step { get; init; }
    public ScreenLayout? Layout { get; init; }
    public IReadOnlyList<UiNode>? Children { get; init; }
    public IReadOnlyList<SelectOption>? Options { get; init; }
    public UiBounds? Bounds { get; init; }
    public UiAction? Action { get; init; }
}
public sealed record SelectOption(string Label, string Value);
public sealed record UiBounds(double X, double Y, double Width, double Height);
public sealed record ScreenLayout(string Kind, double? Width = null, double? Height = null,
    double? ItemWidth = null, double? Gap = null, double? Padding = null);
public sealed record UiAction
{
    public required string Kind { get; init; }
    public string? ScreenId { get; init; }
    public string? CommandId { get; init; }
    public string? EndpointId { get; init; }
    public string? TargetControlId { get; init; }
    public IReadOnlyList<string>? Formats { get; init; }
    public IReadOnlyDictionary<string, ArgumentSource>? Arguments { get; init; }
    public WaitPolicy? Wait { get; init; }
    public UiAction? Then { get; init; }
    public UiAction? OnCompleted { get; init; }
}
public sealed record ArgumentSource(string Source, string? ControlId = null, string? Property = null, JsonElement? Value = null);
public sealed record WaitPolicy(string Block = "control", int TimeoutMs = 30_000);
public sealed record UiCommand(string CommandId, IReadOnlyDictionary<string, ArgumentSource> Arguments, WaitPolicy? Wait = null);

public static class ProtocolJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
            NumberHandling = JsonNumberHandling.Strict,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    public static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);
    public static string SerializeToString<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(ReadOnlySpan<byte> utf8Json, int maxBytes = ProtocolConstants.InitialMaxMessageBytes)
    {
        if (utf8Json.Length > maxBytes)
            throw new ProtocolValidationException("message_too_large", $"Message exceeds {maxBytes} bytes.");
        RejectDuplicateKeys(utf8Json);
        try
        {
            return JsonSerializer.Deserialize<T>(utf8Json, Options)
                ?? throw new ProtocolValidationException("invalid_request", "Message must be a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new ProtocolValidationException("invalid_request", "Message is not valid protocol JSON.", ex);
        }
    }

    private static void RejectDuplicateKeys(ReadOnlySpan<byte> json)
    {
        try
        {
            using var document = JsonDocument.Parse(json.ToArray());
            Check(document.RootElement);
        }
        catch (JsonException ex)
        {
            throw new ProtocolValidationException("invalid_request", "Message is not valid JSON.", ex);
        }
        static void Check(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name)) throw new ProtocolValidationException("invalid_request", $"Duplicate JSON key '{property.Name}'.");
                    Check(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
                foreach (var item in element.EnumerateArray()) Check(item);
            else if (element.ValueKind == JsonValueKind.Number && (!element.TryGetDouble(out var value) || !double.IsFinite(value)))
                throw new ProtocolValidationException("invalid_request", "Numbers must be finite.");
        }
    }
}

public sealed class ProtocolValidationException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}

public static class ProtocolValidator
{
    public static void ValidateRequest(ProtocolRequest request, bool helloCompleted = false, int maxMessageBytes = ProtocolConstants.InitialMaxMessageBytes)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > 128)
            Fail("invalid_request", "requestId must contain 1 to 128 characters.");
        if (string.IsNullOrWhiteSpace(request.ProtocolVersion)) Fail("invalid_request", "protocolVersion is required.");
        if (request.ProtocolVersion != ProtocolConstants.CurrentVersion) Fail("unsupported_version", $"Protocol version '{request.ProtocolVersion}' is not supported.");
        if (string.IsNullOrWhiteSpace(request.Type)) Fail("invalid_request", "type is required.");
        if (request.Payload.ValueKind == JsonValueKind.Undefined) Fail("invalid_request", "payload is required.");
        if (request.Payload.ValueKind != JsonValueKind.Object) Fail("invalid_request", "payload must be a JSON object.");
        if (ProtocolJson.Serialize(request).Length > maxMessageBytes) Fail("message_too_large", "Message exceeds the negotiated limit.");
        if (request.Type == "protocol.hello" && request.AccessToken is not null) Fail("invalid_request", "hello must not contain accessToken.");
        if (!helloCompleted && request.Type != "protocol.hello") Fail("unauthorized", "protocol.hello must complete before other requests.");
        if (!ProtocolConstants.RequestTypes.Contains(request.Type)) return;
        if (helloCompleted && request.Type == "protocol.hello") Fail("invalid_request", "hello has already completed for this connection.");
        if (helloCompleted && request.Type != "auth.login" && string.IsNullOrEmpty(request.AccessToken))
            Fail("unauthorized", "Authentication required.");
    }

    public static void ValidateResponse(ProtocolResponse response, int maxMessageBytes)
    {
        if (string.IsNullOrWhiteSpace(response.RequestId) || response.RequestId.Length > 128) Fail("invalid_request", "Invalid response requestId.");
        if ((response.Result is null) == (response.Error is null)) Fail("invalid_request", "A response must contain exactly one of result or error.");
        if (response.Result is { ValueKind: JsonValueKind.Undefined }) Fail("invalid_request", "result cannot be undefined.");
        if (ProtocolJson.Serialize(response).Length > maxMessageBytes) Fail("message_too_large", "Message exceeds the negotiated limit.");
    }

    public static IReadOnlyList<string> ValidateArguments(ValueDescriptor descriptor, JsonElement value)
    {
        var errors = new List<string>();
        ValidateValue(descriptor, value, "$", errors);
        return errors;
    }

    public static void ValidateCapabilities(CapabilitiesDocument catalog)
    {
        if (catalog.Limits.MaxMessageBytes < ProtocolConstants.InitialMaxMessageBytes) Fail("invalid_request", "maxMessageBytes must be at least 16384.");
        if (catalog.Limits.CommandResponseTimeoutMs <= 0 || catalog.Limits.MinPollIntervalMs < 0 || catalog.Limits.OperationRetentionMs < 0 || catalog.Limits.DeduplicationWindowMs < 0)
            Fail("invalid_request", "Capability time limits are invalid.");
        Unique(catalog.Commands.Select(x => x.Id), "command"); Unique(catalog.Resources.Select(x => x.Id), "resource"); Unique(catalog.Endpoints.Select(x => x.Id), "endpoint");
        foreach (var c in catalog.Commands)
        {
            ValidateDescriptor(c.Parameters, "command parameters"); ValidateDescriptor(c.Result, "command result");
            if (c.Parameters.Type != ValueType.Object || c.Result.Type != ValueType.Object) Fail("invalid_request", "Command parameters and result must be objects.");
        }
        foreach (var endpoint in catalog.Endpoints)
        {
            if (endpoint.Kind is not ("camera" or "chat" or "custom")) Fail("invalid_request", $"Unknown endpoint kind '{endpoint.Kind}'.");
            if (endpoint.Kind is "chat" or "custom" && string.IsNullOrWhiteSpace(endpoint.Contract)) Fail("invalid_request", "Chat and custom endpoints require a contract.");
            ValidateAuth(endpoint.Auth, endpoint.Protocol);
        }
        foreach (var resource in catalog.Resources) ValidateAuth(resource.Auth, "https");
    }

    public static void ValidateUi(UiDocument ui, CapabilitiesDocument capabilities)
    {
        Unique(ui.Screens.Select(x => x.Id), "screen");
        if (ui.Screens.Count == 0)
        {
            if (ui.StartScreenId is not null) Fail("invalid_request", "An empty UI cannot specify startScreenId.");
            return;
        }
        var screens = ui.Screens.ToDictionary(x => x.Id, StringComparer.Ordinal);
        if (ui.StartScreenId is null || !screens.ContainsKey(ui.StartScreenId)) Fail("invalid_request", "startScreenId must reference a screen.");
        var commands = capabilities.Commands.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var resources = capabilities.Resources.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var endpoints = capabilities.Endpoints.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var screen in ui.Screens)
        {
            if (screen.Presentation.Kind is not ("page" or "drawer")) Fail("invalid_request", $"Unknown presentation on '{screen.Id}'.");
            if (screen.Presentation.Kind == "drawer" && screen.Presentation.Side is not ("left" or "right")) Fail("invalid_request", "Drawer side must be left or right.");
            if (screen.EnterCommand is { } enter) ValidateUiCommand(enter, commands, null, true);
            var nodes = new Dictionary<string, UiNode>(StringComparer.Ordinal);
            Collect(screen.Root, nodes);
            ValidateNode(screen.Root, screen, screens, commands, resources, endpoints, nodes);
        }

        void ValidateNode(UiNode node, ScreenDefinition screen, IReadOnlyDictionary<string, ScreenDefinition> allScreens,
            IReadOnlyDictionary<string, CommandDefinition> allCommands, HashSet<string> allResources, HashSet<string> allEndpoints,
            IReadOnlyDictionary<string, UiNode> nodes)
        {
            if (node.Type == "container")
            {
                if (node.Layout is null || node.Children is null) Fail("invalid_request", $"Container '{node.Id}' needs layout and children.");
                if (node.Layout!.Kind is not ("vertical" or "wrap" or "absolute")) return; // Unknown layouts render as unavailable.
                ValidateLayout(node.Layout);
                if (node.Layout.Kind == "absolute")
                {
                    foreach (var child in node.Children!) if (child.Bounds is null) Fail("invalid_request", "Absolute layout children require bounds.");
                }
                else if (node.Layout.Kind == "wrap" && (node.Layout.ItemWidth is null || node.Layout.ItemWidth <= 0)) Fail("invalid_request", "Wrap layout requires positive itemWidth.");
                foreach (var child in node.Children!) ValidateNode(child, screen, allScreens, allCommands, allResources, allEndpoints, nodes);
            }
            else if (node.Type == "image" && (node.ResourceId is null || !allResources.Contains(node.ResourceId))) Fail("invalid_request", $"Image '{node.Id}' references an unknown resource.");
            else if (node.Type == "button")
            {
                if (node.Action is null) Fail("invalid_request", $"Button '{node.Id}' needs an action.");
                ValidateAction(node.Action!, screen, allScreens, allCommands, allEndpoints, nodes);
            }
            else if (node.Type == "select")
            {
                var values = node.Options?.Select(x => x.Value).ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>();
                if (values.Count != (node.Options?.Count ?? 0)) Fail("invalid_request", $"Select '{node.Id}' has duplicate values.");
                if (node.InitialValue is { } selected && (selected.ValueKind != JsonValueKind.String || !values.Contains(selected.GetString()!))) Fail("invalid_request", $"Select '{node.Id}' initialValue must match an option.");
            }
            else if (node.Type == "numberInput" && node.InitialValue is { } number && number.ValueKind != JsonValueKind.Number) Fail("invalid_request", $"numberInput '{node.Id}' initialValue must be numeric.");
            else if (node.Type == "toggle" && node.InitialValue is { } toggle && toggle.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) Fail("invalid_request", $"toggle '{node.Id}' initialValue must be boolean.");
        }
    }

    private static void ValidateAction(UiAction action, ScreenDefinition screen, IReadOnlyDictionary<string, ScreenDefinition> screens,
        IReadOnlyDictionary<string, CommandDefinition> commands, HashSet<string> endpoints, IReadOnlyDictionary<string, UiNode> nodes)
    {
        switch (action.Kind)
        {
            case "navigate": if (action.ScreenId is null || !screens.ContainsKey(action.ScreenId)) Fail("invalid_request", "navigate references an unknown screen."); break;
            case "back": case "closeDrawer": break;
            case "invoke":
                if (action.CommandId is null || !commands.TryGetValue(action.CommandId, out var command)) Fail("invalid_request", "invoke references an unknown command.");
                ValidateUiCommand(new UiCommand(action.CommandId!, action.Arguments ?? new Dictionary<string, ArgumentSource>(), action.Wait), commands, nodes, false);
                break;
            case "openEndpoint": if (action.EndpointId is null || !endpoints.Contains(action.EndpointId)) Fail("invalid_request", "openEndpoint references an unknown endpoint."); break;
            case "scanBarcode":
                if (action.TargetControlId is null || !nodes.TryGetValue(action.TargetControlId, out var target) || target.Type != "textInput") Fail("invalid_request", "scanBarcode target must be a textInput on the same screen.");
                var supported = new HashSet<string>(["ean13", "ean8", "code128", "qr"], StringComparer.Ordinal);
                if (action.Formats is null || action.Formats.Count == 0 || action.Formats.Any(x => !supported.Contains(x))) Fail("invalid_request", "scanBarcode formats are invalid.");
                if (action.Then is not null && action.Then.Kind != "invoke") Fail("invalid_request", "scanBarcode.then must be invoke.");
                if (action.Then is not null) ValidateAction(action.Then, screen, screens, commands, endpoints, nodes);
                break;
            default: break; // Unknown actions are unavailable to the client.
        }
        if (action.Wait is { } wait) ValidateWait(wait, false);
        if (action.OnCompleted is { } followup)
        {
            if (followup.Kind is not ("navigate" or "back" or "closeDrawer")) Fail("invalid_request", "onCompleted supports only navigate, back, and closeDrawer.");
            ValidateAction(followup, screen, screens, commands, endpoints, nodes);
        }
    }

    private static void ValidateUiCommand(UiCommand uiCommand, IReadOnlyDictionary<string, CommandDefinition> commands,
        IReadOnlyDictionary<string, UiNode>? nodes, bool literalOnly)
    {
        if (!commands.TryGetValue(uiCommand.CommandId, out var command)) Fail("invalid_request", $"UI references unknown command '{uiCommand.CommandId}'.");
        if (command is null) return;
        if (uiCommand.Wait is { } wait) ValidateWait(wait, literalOnly);
        var properties = command.Parameters.Properties ?? new Dictionary<string, ValueDescriptor>();
        foreach (var pair in uiCommand.Arguments)
        {
            if (!properties.ContainsKey(pair.Key)) Fail("invalid_request", $"Argument '{pair.Key}' is not declared by command '{uiCommand.CommandId}'.");
            var source = pair.Value;
            if (source.Source == "literal")
            {
                if (source.Value is null) Fail("invalid_request", $"Literal argument '{pair.Key}' requires value.");
                var errors = ValidateArguments(properties[pair.Key], source.Value!.Value);
                if (errors.Count != 0) Fail("invalid_request", $"Invalid literal '{pair.Key}': {errors[0]}.");
            }
            else if (source.Source == "control")
            {
                if (literalOnly) Fail("invalid_request", "enterCommand arguments must be literal.");
                if (source.Property != "value" || source.ControlId is null || nodes is null || !nodes.TryGetValue(source.ControlId, out var node) || node.Type is not ("textInput" or "numberInput" or "toggle" or "select"))
                    Fail("invalid_request", $"Argument '{pair.Key}' references an unavailable control value.");
            }
            else Fail("invalid_request", $"Unknown argument source '{source.Source}'.");
        }
        foreach (var required in command.Parameters.Required ?? []) if (!uiCommand.Arguments.ContainsKey(required)) Fail("invalid_request", $"UI action omits required argument '{required}'.");
    }

    private static void ValidateWait(WaitPolicy wait, bool enter)
    {
        if (wait.TimeoutMs <= 0) Fail("invalid_request", "timeoutMs must be positive.");
        if (wait.Block is not ("none" or "control" or "screen" or "application")) Fail("invalid_request", "Unknown wait block policy.");
        if (enter && wait.Block is not ("none" or "application")) Fail("invalid_request", "enterCommand wait block must be none or application.");
    }

    private static void ValidateDescriptor(ValueDescriptor descriptor, string path)
    {
        if (descriptor.Type == ValueType.Object)
        {
            if (descriptor.Properties is null || descriptor.Required is null || descriptor.AdditionalProperties != false) Fail("invalid_request", $"{path} object must define properties, required, and additionalProperties:false.");
            foreach (var key in descriptor.Required!) if (!descriptor.Properties!.ContainsKey(key)) Fail("invalid_request", $"{path} required property '{key}' is not declared.");
            foreach (var (key, child) in descriptor.Properties!) ValidateDescriptor(child, $"{path}.{key}");
        }
        if (descriptor.Type == ValueType.Array)
        {
            if (descriptor.Items is null) Fail("invalid_request", $"{path} array requires items.");
            ValidateDescriptor(descriptor.Items!, path + "[]");
            if (descriptor.MinItems < 0 || descriptor.MaxItems < 0 || descriptor.MinItems > descriptor.MaxItems) Fail("invalid_request", $"{path} array limits are invalid.");
        }
        if (descriptor.MinLength < 0 || descriptor.MaxLength < 0 || descriptor.MinLength > descriptor.MaxLength) Fail("invalid_request", $"{path} string limits are invalid.");
        if (descriptor.Minimum is { } min && !double.IsFinite(min) || descriptor.Maximum is { } max && !double.IsFinite(max) || descriptor.Minimum > descriptor.Maximum) Fail("invalid_request", $"{path} numeric limits are invalid.");
        if (descriptor.Type == ValueType.Integer && (descriptor.Minimum < -ProtocolConstants.MaxSafeInteger || descriptor.Maximum > ProtocolConstants.MaxSafeInteger)) Fail("invalid_request", $"{path} integer range exceeds JSON safe integers.");
    }

    private static void ValidateValue(ValueDescriptor descriptor, JsonElement value, string path, List<string> errors)
    {
        bool valid = descriptor.Type switch
        {
            ValueType.Object => value.ValueKind == JsonValueKind.Object,
            ValueType.Array => value.ValueKind == JsonValueKind.Array,
            ValueType.String => value.ValueKind == JsonValueKind.String,
            ValueType.Integer => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var integer) && Math.Abs((double)integer) <= ProtocolConstants.MaxSafeInteger,
            ValueType.Number => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number),
            ValueType.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            _ => false
        };
        if (!valid) { errors.Add($"{path} must be {descriptor.Type.ToString().ToLowerInvariant()}"); return; }
        if (descriptor.Type == ValueType.Object)
        {
            var properties = descriptor.Properties ?? new Dictionary<string, ValueDescriptor>();
            foreach (var required in descriptor.Required ?? []) if (!value.TryGetProperty(required, out _)) errors.Add($"{path}.{required} is required");
            foreach (var property in value.EnumerateObject())
            {
                if (!properties.TryGetValue(property.Name, out var child)) errors.Add($"{path}.{property.Name} is not allowed");
                else ValidateValue(child, property.Value, path + "." + property.Name, errors);
            }
        }
        else if (descriptor.Type == ValueType.Array)
        {
            var array = value.EnumerateArray().ToArray();
            if (descriptor.MinItems is { } min && array.Length < min) errors.Add($"{path} must contain at least {min} items");
            if (descriptor.MaxItems is { } max && array.Length > max) errors.Add($"{path} must contain at most {max} items");
            if (descriptor.Items is { } item) for (var i = 0; i < array.Length; i++) ValidateValue(item, array[i], $"{path}[{i}]", errors);
        }
        else if (descriptor.Type == ValueType.String)
        {
            var text = value.GetString()!; var length = text.EnumerateRunes().Count();
            if (descriptor.MinLength is { } min && length < min) errors.Add($"{path} is too short");
            if (descriptor.MaxLength is { } max && length > max) errors.Add($"{path} is too long");
            if (descriptor.Enum is { } values && !values.Any(x => JsonElement.DeepEquals(x, value))) errors.Add($"{path} is not an allowed value");
        }
        else if (value.ValueKind == JsonValueKind.Number)
        {
            var n = value.GetDouble();
            if (descriptor.Minimum is { } min && n < min) errors.Add($"{path} is below minimum {min}");
            if (descriptor.Maximum is { } max && n > max) errors.Add($"{path} exceeds maximum {max}");
        }
    }

    private static void ValidateLayout(ScreenLayout layout)
    {
        if (layout.Kind == "absolute")
        {
            if (layout.Width is null or <= 0 || layout.Height is null or <= 0) Fail("invalid_request", "Absolute layout requires positive width and height.");
        }
        else if (layout.Gap < 0 || layout.Padding < 0) Fail("invalid_request", "Layout gap and padding must be nonnegative.");
        if (layout.Width < 0 || layout.Height < 0 || layout.ItemWidth < 0) Fail("invalid_request", "Layout dimensions must be nonnegative.");
    }

    private static void ValidateAuth(ResourceAuth auth, string protocol)
    {
        if (auth.Mode is not ("none" or "gatewayBearer" or "external")) Fail("invalid_request", "Unknown endpoint authentication mode.");
        if (auth.Mode == "gatewayBearer" && protocol is not ("https" or "wss")) Fail("invalid_request", "gatewayBearer is allowed only for HTTPS/WSS.");
    }

    private static void Collect(UiNode node, Dictionary<string, UiNode> nodes)
    {
        if (!nodes.TryAdd(node.Id, node)) Fail("invalid_request", $"Duplicate UI node id '{node.Id}'.");
        if (node.Type == "container") foreach (var child in node.Children ?? []) Collect(child, nodes);
    }

    private static void Unique(IEnumerable<string> values, string kind)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values) if (string.IsNullOrWhiteSpace(value) || !set.Add(value)) Fail("invalid_request", $"Duplicate or empty {kind} identifier '{value}'.");
    }

    private static void Fail(string code, string message) => throw new ProtocolValidationException(code, message);
}
