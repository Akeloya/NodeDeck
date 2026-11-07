# .NET gateway commands

`GatewayCommand` combines command metadata, argument/result descriptors, a handler,
and a runtime availability policy. Register the complete command with
`GatewayBuilder.AddCommand(command)` or with `gateway.Commands.Add(command)` after
building the server. `CommandDefinition` and `CommandAvailability` remain protocol
DTOs: they describe the snapshot sent to clients, not executable gateway behavior.

```csharp
using System.Net;
using System.Text.Json;
using NodeDeck.Core.Protocol;
using NodeDeck.Gateway;
using NodeDeck.Transport.Tcp;

var emptyObject = new ValueDescriptor
{
    Type = NodeDeck.Core.Protocol.ValueType.Object,
    Properties = new Dictionary<string, ValueDescriptor>(),
    Required = [],
    AdditionalProperties = false
};
var ready = true;
var availability = new DelegateCommandAvailability(() =>
    Volatile.Read(ref ready)
        ? new CommandAvailability(true)
        : new CommandAvailability(false, "Device is offline"));
GatewayCommandHandler handler = (_, _) =>
    ValueTask.FromResult(JsonSerializer.SerializeToElement(new { }));
var command = new GatewayCommand("device.ping", "Ping", null,
    emptyObject, emptyObject, handler, availability);

await using var gateway = new GatewayBuilder("device", "Device", "development-secret")
    .AddCommand(command)
    .Build(new TcpJsonServer(IPAddress.Loopback, 0));
gateway.Start();

Volatile.Write(ref ready, false); // Subsequent invocations are rejected with the reason above.
gateway.Commands.Replace(new GatewayCommand("device.ping", "Ping", null,
    emptyObject, emptyObject, handler)); // Omitting a policy means always available.
gateway.Commands.Remove("device.ping");
gateway.Commands.Add(command);
```

Implement `ICommandAvailability.GetAvailability()` for policies that need their own
dependencies or state. `DelegateCommandAvailability` adapts a function. Policies
run when producing capabilities (including the catalog revision in `ui.get`) and
again before each new invocation. They should be fast, side-effect free, and safe
for concurrent calls. A policy failure prevents execution and returns a protocol
error. Policies do not reserve resources: handlers must still handle state changes
between the check and execution.

The registry supports concurrent readers and writers. `Add` rejects duplicate IDs,
`Replace` requires an existing ID, and `Remove` returns whether a command existed.
`GetCommands()` returns a snapshot of registered commands. Descriptors supplied to a
command must be treated as immutable; use `Replace` to change a command's schema or
handler. Handler and policy instances can be shared between commands.

Each invocation captures one registry snapshot. Replacing or removing a command
does not cancel invocations that already captured it. An identical retried request
keeps its cached result even if registration or availability has since changed.
Each built server has its own registry; later builder changes do not change it.

Registration changes update both the catalog and UI revisions. An invocation using
an old UI revision receives `schema_outdated`. Availability changes update the
catalog revision, while preserving the UI revision so the final availability check
can return `command_unavailable`. `ui.get` references the current catalog revision;
clients must refresh both documents if their revisions do not match. Protocol 0.1
has no change subscriptions, so catalog changes are discovered on refresh.

Migration: replace `AddCommand(definition, handler)` with
`AddCommand(new GatewayCommand(id, title, description, parameters, result, handler,
availability))`. The old two-argument registration overload is removed. Existing
wire messages and clients keep the same format.

## Resolving methods with attributes

`CommandResolver` translates attributed methods into complete `GatewayCommand`
instances and registers them in a supplied `GatewayCommandRegistry`. The registry
owns runtime registration; the resolver handles reflection, schema generation,
argument binding, and availability binding.

```csharp
var commands = new DeviceCommands(new CommandResolver(gateway.Commands));
commands.Init();

public sealed class DeviceCommands(CommandResolver resolver)
{
    public bool IsConnected { get; set; }
    public bool CanPing => IsConnected;

    public void Init() => resolver.Resolve(this);

    [NodeCommand("device.ping", "Ping", Description = "Check the device",
        UnavailableReason = "Device is disconnected")]
    public void Ping() { /* Perform the action. */ }

    private bool IsReady() => IsConnected;

    [NodeCommand("device.echo", "Echo", CanExecute = nameof(IsReady))]
    public Task<string> Echo(string message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(message);
    }

    public Func<bool> AllowReset { get; set; } = () => true;

    [NodeCommand("device.reset", "Reset", CanExecute = nameof(AllowReset))]
    public void Reset() { /* Perform the action. */ }
}
```

By default the resolver uses an optional `bool Can{MethodName}` property. Without
that property the command is always available. `CanExecute = nameof(Member)`
overrides the convention and can name a bool property, a parameterless bool method,
or a `Func<bool>` property on the owner. C# attribute arguments cannot contain
delegates or lambdas, so the attribute names the member supplying the check.
The member is evaluated on each availability check, including reading the current
delegate from a delegate property. Invalid or missing explicit bindings fail during
resolution; they never silently enable a command.

Supported signatures:

- Public or non-public instance methods, including inherited visible methods and
  overrides with inherited attributes. Only attributed methods are exposed.
- `string`, `bool`, `int`, `long`, `float`, `double`, `decimal`, and arrays of these
  types for parameters and return values. Parameter names become JSON field names.
  `long` is restricted to the protocol's JSON-safe integer range.
- Optional parameters with non-null defaults. Required parameters appear in the
  generated schema's `required` array.
- One optional `CancellationToken` parameter, injected by the gateway and omitted
  from the wire schema.
- `void`, `Task`, and `ValueTask` produce an empty object result. A supported return
  value `T`, `Task<T>`, or `ValueTask<T>` produces `{ "value": ... }`.

Null values, arbitrary object DTOs, static/generic methods, `ref`/`out` parameters,
and `async void` are unsupported. Use a manually constructed `GatewayCommand` for
custom object schemas and serialization. Resolution validates the entire owner
before publishing commands; duplicate IDs also reject the entire batch. Calling
`Resolve` twice for the same registered IDs is an error. Remove or replace commands
through the registry. Registered handlers retain their owner, and the owner remains
responsible for thread safety and disposal of its dependencies.

The console's `ProjectCommands` demonstrates `Init()` calling `Resolve(this)` and
the `CanOpenProjectIssues` convention. It publishes `project.openIssues` with no
arguments. The existing mobile client renders its title as a button automatically
from `capabilities.commands` when connecting. Pressing it opens
`https://github.com/Akeloya/NodeDeck/issues` in the gateway computer's default browser.
Registration itself does not launch the browser.

## Command names and descriptions from resources

Use the resource constructor instead of a literal title:

```csharp
public sealed class ProjectCommands
{
    [NodeCommand("project.openIssues", typeof(CommandTexts))]
    public void OpenIssues() { /* Open the project issues. */ }
}

public static class CommandTexts
{
    public static string ProjectCommands_OpenIssues_name => "Project issues";
    public static string ProjectCommands_OpenIssues_description => "Open the project's issues";

    // A distinct command owner named projectCommands can use this same resource type.
    public static string projectCommands_OpenIssues_name => "Other project issues";
    public static string projectCommands_OpenIssues_description => "Open the other project's issues";
}
```

The keys are `{ClassName}_{MethodName}_name` and
`{ClassName}_{MethodName}_description`, preserving the exact case of both names.
`ClassName` is the runtime type name of the object passed to `Resolve`, including
when its method is inherited. The resolver first looks for readable static string
properties (public or internal/private) on the resource type. Their getters can
return localized strings from a strongly typed resource class.

If the name property does not exist, the resolver uses `ResourceManager(resourceType)`
to read embedded resources whose base name matches the resource type's full name.
It uses case-sensitive lookup and `CurrentUICulture`, with normal culture fallback.
Text is captured during `Resolve`; changing culture later requires resolving and
registering the commands again. A missing or blank name rejects the whole batch;
a missing description is allowed. An explicit `Description` on the attribute
overrides the resource description.

The standard .NET `.resx` compiler rejects keys that differ only in case. For such
names in one resource type, expose exact-case static properties as above; if they
need `.resx` translations, forward each property to a distinct underlying resource
key (for example `UpperProjectIssuesName` and `LowerProjectIssuesName`). This keeps
`ProjectCommands` and `projectCommands` independent without losing strings during
resource compilation.

## Device sessions

Pass a persisted installation ID as `clientId` to `NodeDeckClientConnection.ConnectAsync` for every transport. If omitted, the SDK generates a new ID for that connection. Android persists one ID across its profiles. Optionally pass `expectedGatewayId` to check hello before sending the secret.

`gateway.ActiveSessions` returns immutable snapshots with device identity, subject, transport and timestamps, excluding bearer tokens. Call `gateway.EndSession(sessionId)` to revoke a session; the next authenticated request on that connection receives `unauthorized`. A new login from the same device atomically replaces the previous session. Disconnect and logout remove it as well.

`GatewayBuilder.WithSessions(new GatewaySessionOptions { ... })` configures the ping interval (default one minute), inactivity timeout (five minutes), and active-user log interval (one minute). The timeout must exceed the ping interval. The client answers gateway pings in its receive loop while commands execute. `client.Disconnected` completes when the receive loop terminates; disposing a client attempts logout and releases the transport. Never automatically replay an interrupted command.

## Gateway factory and routing nodes

`GatewayFactory.CreateAsync` returns `IGateway` for either `GatewayMode.Executor` (`GatewayServer`) or `GatewayMode.Node` (`GatewayNode`). The host uses the same lifecycle and metadata API without casting to an implementation:

```csharp
var transports = new ProtocolMessageListenerFactory()
    .Register<TcpListenerSettings>(settings => new TcpJsonServer(settings));
var factory = new GatewayFactory(transports);
var settings = factory.LoadSettings("gateway.json");
await using IGateway gateway = await factory.CreateAsync(settings,
    settings.Mode == GatewayMode.Executor ? commands => commands.Add(command) : null);
gateway.UpdateMetadata("Warehouse workstation", "Dispatch and inventory commands");
gateway.Start();
await gateway.Ready;
await gateway.RunAsync(cancellationToken);
```

`GatewaySettings` contains the role, stable ID, default name/description, shared secret, listener settings, session settings and (for a node) peer settings. Embedded `Listeners` use the same transport registration as standalone listener JSON. Register Windows BLE in the host when needed. The legacy `GatewayBuilder` remains a convenience for code that constructs a command executor directly; new configuration-driven hosts use the common factory.

The optional registry callback is for executor commands only. Register attributed commands there with `new CommandResolver(commands)`. A node owns its connection commands and rejects a local command-registration callback.

`IGateway.ConnectedGateways` returns authenticated peer snapshots (empty for executors). A node periodically probes configured peers and exposes their current metadata and availability in its directory. Each phone chooses its own peer; subsequent application requests are forwarded to that peer with the original request ID and device identity. An executor runs the command locally. Nodes can nest, but visited IDs and a maximum of 16 forwards prevent cycles and backward routing. An unavailable peer produces an error instead of automatic failover or command replay. Revoking the phone's session also releases its selected downstream connection.

`CommandExecutionResult.RefreshCapabilities` tells clients to reload their catalog after selection, return or a route failure. Android implements this flow and displays gateway and connection-command descriptions. See [transport setup](../../protocol/tcp-development.md) for complete JSON examples and [wire protocol](../../protocol/wire.md) for routing fields and errors.
