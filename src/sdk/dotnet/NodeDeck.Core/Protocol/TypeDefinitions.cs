using System.Collections.Frozen;
using System.Text.Json;

namespace NodeDeck.Core.Protocol;

public static class ProtocolConstants
{
    public const string CurrentVersion = "0.1";
    public const int InitialMaxMessageBytes = 16_384;
    public const long MaxSafeInteger = 9_007_199_254_740_991;
    public const int MaxRequestIdLength = 128;
    public const int DefaultWaitTimeoutMs = 30_000;
    public const byte FrameDelimiter = (byte)'\n';
    public const byte CarriageReturn = (byte)'\r';

    public static readonly IReadOnlySet<string> RequestTypes = new[]
    {
        RequestType.ProtocolHello, RequestType.AuthLogin, RequestType.AuthLogout, RequestType.CapabilitiesGet,
        RequestType.UiGet, RequestType.CommandInvoke, RequestType.OperationGet
    }.ToFrozenSet(StringComparer.Ordinal);
}

public static class BleProtocolConstants
{
    public const int ChunkSize = 20;
    public const string ClientConfigurationDescriptorUuid = "00002902-0000-1000-8000-00805f9b34fb";
    public const string ServiceUuid = "a1b2c3d4-e5f6-4a1b-8c9d-001122334455";
    public const string ClientToGatewayCharacteristicUuid = "a1b2c3d4-e5f6-4a1b-8c9d-001122334456";
    public const string GatewayToClientCharacteristicUuid = "a1b2c3d4-e5f6-4a1b-8c9d-001122334457";
}

public static class RequestType
{
    // Gateway-to-client request; not part of the client-to-gateway RequestTypes set.
    public const string SessionPing = "session.ping";
    public const string ProtocolHello = "protocol.hello";
    public const string AuthLogin = "auth.login";
    public const string AuthLogout = "auth.logout";
    public const string CapabilitiesGet = "capabilities.get";
    public const string UiGet = "ui.get";
    public const string CommandInvoke = "command.invoke";
    public const string OperationGet = "operation.get";
}

public static class ProtocolErrorCode
{
    public const string RouteLoop = "route_loop";
    public const string RouteHopLimit = "route_hop_limit";
    public const string GatewayUnavailable = "gateway_unavailable";
    public const string InvalidRequest = "invalid_request";
    public const string InvalidResponse = "invalid_response";
    public const string UnsupportedVersion = "unsupported_version";
    public const string UnsupportedType = "unsupported_type";
    public const string Unauthorized = "unauthorized";
    public const string Forbidden = "forbidden";
    public const string RateLimited = "rate_limited";
    public const string MessageTooLarge = "message_too_large";
    public const string CommandNotFound = "command_not_found";
    public const string InvalidArguments = "invalid_arguments";
    public const string CommandUnavailable = "command_unavailable";
    public const string SchemaOutdated = "schema_outdated";
    public const string RequestConflict = "request_conflict";
    public const string OperationNotFound = "operation_not_found";
    public const string ExecutionFailed = "execution_failed";
    public const string InternalError = "internal_error";
}

public static class ProtocolFeature
{
    public const string Commands = "commands";
    public const string OperationsPoll = "operations.poll";
    public const string Ui = "ui";
    public const string ResourcesRead = "resources.read";
    public const string Endpoints = "endpoints";
}

public static class AuthMethod
{
    public const string SharedSecret = "sharedSecret";
}

public static class ExecutionStatus
{
    public const string Accepted = "accepted";
    public const string Rejected = "rejected";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Queued = "queued";
    public const string Running = "running";
}

public static class EndpointKind
{
    public const string Camera = "camera";
    public const string Chat = "chat";
    public const string Custom = "custom";
}

public static class EndpointProtocol
{
    public const string Https = "https";
    public const string Wss = "wss";
    public const string Rtsp = "rtsp";
}

public static class ResourceAuthMode
{
    public const string None = "none";
    public const string GatewayBearer = "gatewayBearer";
    public const string External = "external";
}

public static class PresentationKind
{
    public const string Page = "page";
    public const string Drawer = "drawer";
}

public static class DrawerSide
{
    public const string Left = "left";
    public const string Right = "right";
}

public static class LayoutKind
{
    public const string Vertical = "vertical";
    public const string Wrap = "wrap";
    public const string Absolute = "absolute";
}

public static class UiNodeType
{
    public const string Container = "container";
    public const string Label = "label";
    public const string Button = "button";
    public const string TextInput = "textInput";
    public const string NumberInput = "numberInput";
    public const string Toggle = "toggle";
    public const string Select = "select";
    public const string Image = "image";
}

public static class UiActionKind
{
    public const string Navigate = "navigate";
    public const string Back = "back";
    public const string CloseDrawer = "closeDrawer";
    public const string Invoke = "invoke";
    public const string ScanBarcode = "scanBarcode";
    public const string OpenEndpoint = "openEndpoint";
}

public static class BarcodeFormat
{
    public const string Ean13 = "ean13";
    public const string Ean8 = "ean8";
    public const string Code128 = "code128";
    public const string Qr = "qr";

    public static readonly IReadOnlySet<string> Supported =
        new[] { Ean13, Ean8, Code128, Qr }.ToFrozenSet(StringComparer.Ordinal);
}

public static class ArgumentSourceKind
{
    public const string Literal = "literal";
    public const string Control = "control";
}

public static class ControlProperty
{
    public const string Value = "value";
}

public static class WaitBlock
{
    public const string None = "none";
    public const string Control = "control";
    public const string Screen = "screen";
    public const string Application = "application";
}

public static class ProtocolField
{
    public const string Commands = "commands";
    public const string Error = "error";
    public const string Output = "output";
    public const string Status = "status";
    public const string Code = "code";
    public const string AccessToken = "accessToken";
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
    string? AccessToken = null,
    GatewayRoute? Route = null);

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
    string? OperationToken = null, int? PollAfterMs = null, bool? RefreshCapabilities = null);
public sealed record OperationGetPayload(string OperationToken);
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
public sealed record GatewayInfo(string Id, string Name, string? Description = null, string Kind = "executor");
public sealed record ProtocolLimits(int MaxMessageBytes, int CommandResponseTimeoutMs, int MinPollIntervalMs,
    long OperationRetentionMs, long DeduplicationWindowMs, bool DeduplicationPersistent)
{
    public static ProtocolLimits Default { get; } = new(
        MaxMessageBytes: 262_144, CommandResponseTimeoutMs: 1_000, MinPollIntervalMs: 250,
        OperationRetentionMs: 86_400_000, DeduplicationWindowMs: 86_400_000, DeduplicationPersistent: false);
}
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
public sealed record WaitPolicy(string Block = WaitBlock.Control, int TimeoutMs = ProtocolConstants.DefaultWaitTimeoutMs);
public sealed record UiCommand(string CommandId, IReadOnlyDictionary<string, ArgumentSource> Arguments, WaitPolicy? Wait = null);
