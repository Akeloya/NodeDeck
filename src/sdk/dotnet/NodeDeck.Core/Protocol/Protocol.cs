using System.Text.Json;
using System.Text.Json.Serialization;

namespace NodeDeck.Core.Protocol;

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
            throw new MessageTooLargeException($"Message exceeds {maxBytes} bytes.");
        RejectDuplicateKeys(utf8Json);
        try
        {
            return JsonSerializer.Deserialize<T>(utf8Json, Options)
                ?? throw new InvalidRequestException("Message must be a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new InvalidRequestException("Message is not valid protocol JSON.", ex);
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
            throw new InvalidRequestException("Message is not valid JSON.", ex);
        }
        static void Check(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                        throw new InvalidRequestException($"Duplicate JSON key '{property.Name}'.");
                    Check(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
                foreach (var item in element.EnumerateArray())
                    Check(item);
            else if (element.ValueKind == JsonValueKind.Number && (!element.TryGetDouble(out var value) || !double.IsFinite(value)))
                throw new InvalidRequestException("Numbers must be finite.");
        }
    }
}

public static class ProtocolValidator
{
    public static void ValidateRequest(ProtocolRequest request, bool helloCompleted = false, int maxMessageBytes = ProtocolConstants.InitialMaxMessageBytes)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > ProtocolConstants.MaxRequestIdLength)
            throw new InvalidRequestException($"requestId must contain 1 to {ProtocolConstants.MaxRequestIdLength} characters.");
        if (string.IsNullOrWhiteSpace(request.ProtocolVersion))
            throw new InvalidRequestException("protocolVersion is required.");
        if (request.ProtocolVersion != ProtocolConstants.CurrentVersion)
            throw new UnsupportedVersionException($"Protocol version '{request.ProtocolVersion}' is not supported.");
        if (string.IsNullOrWhiteSpace(request.Type))
            throw new InvalidRequestException("type is required.");
        if (request.Payload.ValueKind == JsonValueKind.Undefined)
            throw new InvalidRequestException("payload is required.");
        if (request.Payload.ValueKind != JsonValueKind.Object)
            throw new InvalidRequestException("payload must be a JSON object.");
        if (ProtocolJson.Serialize(request).Length > maxMessageBytes)
            throw new MessageTooLargeException("Message exceeds the negotiated limit.");
        if (request.Type == RequestType.ProtocolHello && request.AccessToken is not null)
            throw new InvalidRequestException("hello must not contain accessToken.");
        if (!helloCompleted && request.Type != RequestType.ProtocolHello)
            throw new UnauthorizedException($"{RequestType.ProtocolHello} must complete before other requests.");
        if (!ProtocolConstants.RequestTypes.Contains(request.Type))
            return;
        if (helloCompleted && request.Type == RequestType.ProtocolHello)
            throw new InvalidRequestException("hello has already completed for this connection.");
        if (helloCompleted && request.Type != RequestType.AuthLogin && string.IsNullOrEmpty(request.AccessToken))
            throw new UnauthorizedException("Authentication required.");
    }

    public static void ValidateResponse(ProtocolResponse response, int maxMessageBytes)
    {
        if (string.IsNullOrWhiteSpace(response.RequestId) || response.RequestId.Length > ProtocolConstants.MaxRequestIdLength)
            throw new InvalidRequestException("Invalid response requestId.");
        if ((response.Result is null) == (response.Error is null))
            throw new InvalidRequestException("A response must contain exactly one of result or error.");
        if (response.Result is { ValueKind: JsonValueKind.Undefined })
            throw new InvalidRequestException("result cannot be undefined.");
        if (ProtocolJson.Serialize(response).Length > maxMessageBytes)
            throw new MessageTooLargeException("Message exceeds the negotiated limit.");
    }

    public static IReadOnlyList<string> ValidateArguments(ValueDescriptor descriptor, JsonElement value)
    {
        var errors = new List<string>();
        ValidateValue(descriptor, value, "$", errors);
        return errors;
    }

    public static void ValidateCapabilities(CapabilitiesDocument catalog)
    {
        if (catalog.Limits.MaxMessageBytes < ProtocolConstants.InitialMaxMessageBytes)
            throw new InvalidRequestException($"maxMessageBytes must be at least {ProtocolConstants.InitialMaxMessageBytes}.");
        if (catalog.Limits.CommandResponseTimeoutMs <= 0 || catalog.Limits.MinPollIntervalMs < 0 || catalog.Limits.OperationRetentionMs < 0 || catalog.Limits.DeduplicationWindowMs < 0)
            throw new InvalidRequestException("Capability time limits are invalid.");
        Unique(catalog.Commands.Select(x => x.Id), "command");
        Unique(catalog.Resources.Select(x => x.Id), "resource");
        Unique(catalog.Endpoints.Select(x => x.Id), "endpoint");
        foreach (var c in catalog.Commands)
        {
            ValidateDescriptor(c.Parameters, "command parameters");
            ValidateDescriptor(c.Result, "command result");
            if (c.Parameters.Type != ValueType.Object || c.Result.Type != ValueType.Object)
                throw new InvalidRequestException("Command parameters and result must be objects.");
        }
        foreach (var endpoint in catalog.Endpoints)
        {
            if (endpoint.Kind is not (EndpointKind.Camera or EndpointKind.Chat or EndpointKind.Custom))
                throw new InvalidRequestException($"Unknown endpoint kind '{endpoint.Kind}'.");
            if (endpoint.Kind is EndpointKind.Chat or EndpointKind.Custom && string.IsNullOrWhiteSpace(endpoint.Contract))
                throw new InvalidRequestException("Chat and custom endpoints require a contract.");
            ValidateAuth(endpoint.Auth, endpoint.Protocol);
        }
        foreach (var resource in catalog.Resources)
            ValidateAuth(resource.Auth, EndpointProtocol.Https);
    }

    public static void ValidateUi(UiDocument ui, CapabilitiesDocument capabilities)
    {
        Unique(ui.Screens.Select(x => x.Id), "screen");
        if (ui.Screens.Count == 0)
        {
            if (ui.StartScreenId is not null)
                throw new InvalidRequestException("An empty UI cannot specify startScreenId.");
            return;
        }
        var screens = ui.Screens.ToDictionary(x => x.Id, StringComparer.Ordinal);
        if (ui.StartScreenId is null || !screens.ContainsKey(ui.StartScreenId))
            throw new InvalidRequestException("startScreenId must reference a screen.");
        var commands = capabilities.Commands.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var resources = capabilities.Resources.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var endpoints = capabilities.Endpoints.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var screen in ui.Screens)
        {
            if (screen.Presentation.Kind is not (PresentationKind.Page or PresentationKind.Drawer))
                throw new InvalidRequestException($"Unknown presentation on '{screen.Id}'.");
            if (screen.Presentation.Kind == PresentationKind.Drawer && screen.Presentation.Side is not (DrawerSide.Left or DrawerSide.Right))
                throw new InvalidRequestException("Drawer side must be left or right.");
            if (screen.EnterCommand is { } enter)
                ValidateUiCommand(enter, commands, null, true);
            var nodes = new Dictionary<string, UiNode>(StringComparer.Ordinal);
            Collect(screen.Root, nodes);
            ValidateNode(screen.Root, screen, screens, commands, resources, endpoints, nodes);
        }

        void ValidateNode(UiNode node, ScreenDefinition screen, IReadOnlyDictionary<string, ScreenDefinition> allScreens,
            IReadOnlyDictionary<string, CommandDefinition> allCommands, HashSet<string> allResources, HashSet<string> allEndpoints,
            IReadOnlyDictionary<string, UiNode> nodes)
        {
            if (node.Type == UiNodeType.Container)
            {
                if (node.Layout is null || node.Children is null)
                    throw new InvalidRequestException($"Container '{node.Id}' needs layout and children.");
                if (node.Layout.Kind is not (LayoutKind.Vertical or LayoutKind.Wrap or LayoutKind.Absolute))
                    return; // Unknown layouts render as unavailable.
                ValidateLayout(node.Layout);
                if (node.Layout.Kind == LayoutKind.Absolute)
                {
                    foreach (var child in node.Children)
                        if (child.Bounds is null)
                            throw new InvalidRequestException("Absolute layout children require bounds.");
                }
                else if (node.Layout.Kind == LayoutKind.Wrap && (node.Layout.ItemWidth is null || node.Layout.ItemWidth <= 0))
                    throw new InvalidRequestException("Wrap layout requires positive itemWidth.");
                foreach (var child in node.Children)
                    ValidateNode(child, screen, allScreens, allCommands, allResources, allEndpoints, nodes);
            }
            else if (node.Type == UiNodeType.Image && (node.ResourceId is null || !allResources.Contains(node.ResourceId)))
                throw new InvalidRequestException($"Image '{node.Id}' references an unknown resource.");
            else if (node.Type == UiNodeType.Button)
            {
                if (node.Action is null)
                    throw new InvalidRequestException($"Button '{node.Id}' needs an action.");
                ValidateAction(node.Action, screen, allScreens, allCommands, allEndpoints, nodes);
            }
            else if (node.Type == UiNodeType.Select)
            {
                var values = node.Options?.Select(x => x.Value).ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>();
                if (values.Count != (node.Options?.Count ?? 0))
                    throw new InvalidRequestException($"Select '{node.Id}' has duplicate values.");
                if (node.InitialValue is { } selected && (selected.ValueKind != JsonValueKind.String || !values.Contains(selected.GetString()!)))
                    throw new InvalidRequestException($"Select '{node.Id}' initialValue must match an option.");
            }
            else if (node.Type == UiNodeType.NumberInput && node.InitialValue is { } number && number.ValueKind != JsonValueKind.Number)
                throw new InvalidRequestException($"numberInput '{node.Id}' initialValue must be numeric.");
            else if (node.Type == UiNodeType.Toggle && node.InitialValue is { } toggle && toggle.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidRequestException($"toggle '{node.Id}' initialValue must be boolean.");
        }
    }

    private static void ValidateAction(UiAction action, ScreenDefinition screen, IReadOnlyDictionary<string, ScreenDefinition> screens,
        IReadOnlyDictionary<string, CommandDefinition> commands, HashSet<string> endpoints, IReadOnlyDictionary<string, UiNode> nodes)
    {
        switch (action.Kind)
        {
            case UiActionKind.Navigate:
                if (action.ScreenId is null || !screens.ContainsKey(action.ScreenId))
                    throw new InvalidRequestException("navigate references an unknown screen.");
                break;
            case UiActionKind.Back:
            case UiActionKind.CloseDrawer:
                break;
            case UiActionKind.Invoke:
                if (action.CommandId is null || !commands.TryGetValue(action.CommandId, out var command))
                    throw new InvalidRequestException("invoke references an unknown command.");
                ValidateUiCommand(new UiCommand(action.CommandId, action.Arguments ?? new Dictionary<string, ArgumentSource>(), action.Wait), commands, nodes, false);
                break;
            case UiActionKind.OpenEndpoint:
                if (action.EndpointId is null || !endpoints.Contains(action.EndpointId))
                    throw new InvalidRequestException("openEndpoint references an unknown endpoint.");
                break;
            case UiActionKind.ScanBarcode:
                if (action.TargetControlId is null || !nodes.TryGetValue(action.TargetControlId, out var target) || target.Type != UiNodeType.TextInput)
                    throw new InvalidRequestException("scanBarcode target must be a textInput on the same screen.");
                if (action.Formats is null || action.Formats.Count == 0 || action.Formats.Any(x => !BarcodeFormat.Supported.Contains(x)))
                    throw new InvalidRequestException("scanBarcode formats are invalid.");
                if (action.Then is not null && action.Then.Kind != UiActionKind.Invoke)
                    throw new InvalidRequestException("scanBarcode.then must be invoke.");
                if (action.Then is not null)
                    ValidateAction(action.Then, screen, screens, commands, endpoints, nodes);
                break;
            default:
                break; // Unknown actions are unavailable to the client.
        }
        if (action.Wait is { } wait)
            ValidateWait(wait, false);
        if (action.OnCompleted is { } followup)
        {
            if (followup.Kind is not (UiActionKind.Navigate or UiActionKind.Back or UiActionKind.CloseDrawer))
                throw new InvalidRequestException("onCompleted supports only navigate, back, and closeDrawer.");
            ValidateAction(followup, screen, screens, commands, endpoints, nodes);
        }
    }

    private static void ValidateUiCommand(UiCommand uiCommand, IReadOnlyDictionary<string, CommandDefinition> commands,
        IReadOnlyDictionary<string, UiNode>? nodes, bool literalOnly)
    {
        if (!commands.TryGetValue(uiCommand.CommandId, out var command))
            throw new InvalidRequestException($"UI references unknown command '{uiCommand.CommandId}'.");
        if (uiCommand.Wait is { } wait)
            ValidateWait(wait, literalOnly);
        var properties = command.Parameters.Properties ?? new Dictionary<string, ValueDescriptor>();
        foreach (var pair in uiCommand.Arguments)
        {
            if (!properties.ContainsKey(pair.Key))
                throw new InvalidRequestException($"Argument '{pair.Key}' is not declared by command '{uiCommand.CommandId}'.");
            var source = pair.Value;
            if (source.Source == ArgumentSourceKind.Literal)
            {
                if (source.Value is null)
                    throw new InvalidRequestException($"Literal argument '{pair.Key}' requires value.");
                var errors = ValidateArguments(properties[pair.Key], source.Value.Value);
                if (errors.Count != 0)
                    throw new InvalidRequestException($"Invalid literal '{pair.Key}': {errors[0]}.");
            }
            else if (source.Source == ArgumentSourceKind.Control)
            {
                if (literalOnly)
                    throw new InvalidRequestException("enterCommand arguments must be literal.");
                if (source.Property != ControlProperty.Value || source.ControlId is null || nodes is null || !nodes.TryGetValue(source.ControlId, out var node) || node.Type is not (UiNodeType.TextInput or UiNodeType.NumberInput or UiNodeType.Toggle or UiNodeType.Select))
                    throw new InvalidRequestException($"Argument '{pair.Key}' references an unavailable control value.");
            }
            else
                throw new InvalidRequestException($"Unknown argument source '{source.Source}'.");
        }
        foreach (var required in command.Parameters.Required ?? [])
            if (!uiCommand.Arguments.ContainsKey(required))
                throw new InvalidRequestException($"UI action omits required argument '{required}'.");
    }

    private static void ValidateWait(WaitPolicy wait, bool enter)
    {
        if (wait.TimeoutMs <= 0)
            throw new InvalidRequestException("timeoutMs must be positive.");
        if (wait.Block is not (WaitBlock.None or WaitBlock.Control or WaitBlock.Screen or WaitBlock.Application))
            throw new InvalidRequestException("Unknown wait block policy.");
        if (enter && wait.Block is not (WaitBlock.None or WaitBlock.Application))
            throw new InvalidRequestException("enterCommand wait block must be none or application.");
    }

    private static void ValidateDescriptor(ValueDescriptor descriptor, string path)
    {
        if (descriptor.Type == ValueType.Object)
        {
            if (descriptor.Properties is null || descriptor.Required is null || descriptor.AdditionalProperties != false)
                throw new InvalidRequestException($"{path} object must define properties, required, and additionalProperties:false.");
            foreach (var key in descriptor.Required)
                if (!descriptor.Properties.ContainsKey(key))
                    throw new InvalidRequestException($"{path} required property '{key}' is not declared.");
            foreach (var (key, child) in descriptor.Properties)
                ValidateDescriptor(child, $"{path}.{key}");
        }
        if (descriptor.Type == ValueType.Array)
        {
            if (descriptor.Items is null)
                throw new InvalidRequestException($"{path} array requires items.");
            ValidateDescriptor(descriptor.Items, path + "[]");
            if (descriptor.MinItems < 0 || descriptor.MaxItems < 0 || descriptor.MinItems > descriptor.MaxItems)
                throw new InvalidRequestException($"{path} array limits are invalid.");
        }
        if (descriptor.MinLength < 0 || descriptor.MaxLength < 0 || descriptor.MinLength > descriptor.MaxLength)
            throw new InvalidRequestException($"{path} string limits are invalid.");
        if (descriptor.Minimum is { } min && !double.IsFinite(min) || descriptor.Maximum is { } max && !double.IsFinite(max) || descriptor.Minimum > descriptor.Maximum)
            throw new InvalidRequestException($"{path} numeric limits are invalid.");
        if (descriptor.Type == ValueType.Integer && (descriptor.Minimum < -ProtocolConstants.MaxSafeInteger || descriptor.Maximum > ProtocolConstants.MaxSafeInteger))
            throw new InvalidRequestException($"{path} integer range exceeds JSON safe integers.");
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
        if (!valid)
        {
            errors.Add($"{path} must be {descriptor.Type.ToString().ToLowerInvariant()}");
            return;
        }
        if (descriptor.Type == ValueType.Object)
        {
            var properties = descriptor.Properties ?? new Dictionary<string, ValueDescriptor>();
            foreach (var required in descriptor.Required ?? [])
                if (!value.TryGetProperty(required, out _))
                    errors.Add($"{path}.{required} is required");
            foreach (var property in value.EnumerateObject())
            {
                if (!properties.TryGetValue(property.Name, out var child))
                    errors.Add($"{path}.{property.Name} is not allowed");
                else
                    ValidateValue(child, property.Value, path + "." + property.Name, errors);
            }
        }
        else if (descriptor.Type == ValueType.Array)
        {
            var array = value.EnumerateArray().ToArray();
            if (descriptor.MinItems is { } min && array.Length < min)
                errors.Add($"{path} must contain at least {min} items");
            if (descriptor.MaxItems is { } max && array.Length > max)
                errors.Add($"{path} must contain at most {max} items");
            if (descriptor.Items is { } item)
                for (var i = 0; i < array.Length; i++)
                    ValidateValue(item, array[i], $"{path}[{i}]", errors);
        }
        else if (descriptor.Type == ValueType.String)
        {
            var text = value.GetString()!;
            var length = text.EnumerateRunes().Count();
            if (descriptor.MinLength is { } min && length < min)
                errors.Add($"{path} is too short");
            if (descriptor.MaxLength is { } max && length > max)
                errors.Add($"{path} is too long");
            if (descriptor.Enum is { } values && !values.Any(x => JsonElement.DeepEquals(x, value)))
                errors.Add($"{path} is not an allowed value");
        }
        else if (value.ValueKind == JsonValueKind.Number)
        {
            var n = value.GetDouble();
            if (descriptor.Minimum is { } min && n < min)
                errors.Add($"{path} is below minimum {min}");
            if (descriptor.Maximum is { } max && n > max)
                errors.Add($"{path} exceeds maximum {max}");
        }
    }

    private static void ValidateLayout(ScreenLayout layout)
    {
        if (layout.Kind == LayoutKind.Absolute)
        {
            if (layout.Width is null or <= 0 || layout.Height is null or <= 0)
                throw new InvalidRequestException("Absolute layout requires positive width and height.");
        }
        else if (layout.Gap < 0 || layout.Padding < 0)
            throw new InvalidRequestException("Layout gap and padding must be nonnegative.");
        if (layout.Width < 0 || layout.Height < 0 || layout.ItemWidth < 0)
            throw new InvalidRequestException("Layout dimensions must be nonnegative.");
    }

    private static void ValidateAuth(ResourceAuth auth, string protocol)
    {
        if (auth.Mode is not (ResourceAuthMode.None or ResourceAuthMode.GatewayBearer or ResourceAuthMode.External))
            throw new InvalidRequestException("Unknown endpoint authentication mode.");
        if (auth.Mode == ResourceAuthMode.GatewayBearer && protocol is not (EndpointProtocol.Https or EndpointProtocol.Wss))
            throw new InvalidRequestException("gatewayBearer is allowed only for HTTPS/WSS.");
    }

    private static void Collect(UiNode node, Dictionary<string, UiNode> nodes)
    {
        if (!nodes.TryAdd(node.Id, node))
            throw new InvalidRequestException($"Duplicate UI node id '{node.Id}'.");
        if (node.Type == UiNodeType.Container)
            foreach (var child in node.Children ?? [])
                Collect(child, nodes);
    }

    private static void Unique(IEnumerable<string> values, string kind)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
            if (string.IsNullOrWhiteSpace(value) || !set.Add(value))
                throw new InvalidRequestException($"Duplicate or empty {kind} identifier '{value}'.");
    }
}
