using System.Net;
using System.Text.Json;
using NodeDeck.Client;
using NodeDeck.Core.Protocol;
using NodeDeck.Gateway;
using NodeDeck.Transport.Tcp;
using NUnit.Framework;
using ProtocolValueType = NodeDeck.Core.Protocol.ValueType;

namespace NodeDeck.Tests;

public class GatewayCommandTests
{
    private static readonly ValueDescriptor Arguments = new()
    {
        Type = ProtocolValueType.Object, Properties = new Dictionary<string, ValueDescriptor>(),
        Required = [], AdditionalProperties = false
    };
    private static readonly ValueDescriptor Result = new()
    {
        Type = ProtocolValueType.Object,
        Properties = new Dictionary<string, ValueDescriptor> { ["value"] = new() { Type = ProtocolValueType.String } },
        Required = ["value"], AdditionalProperties = false
    };

    [Test]
    public async Task RunningGateway_UpdatesCommandsCatalogAndUiWithoutReconnect()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = timeout.Token;
        await using var gateway = CreateBuilder().Build(new TcpJsonServer(IPAddress.Loopback, 0));
        gateway.Start();
        await using var client = await Connect(gateway, ct);
        var initial = await GetCapabilities(client, ct);
        var initialUi = await GetUi(client, ct);

        gateway.Commands.Add(CreateCommand("first"));
        var added = await GetCapabilities(client, ct);
        var addedUi = await GetUi(client, ct);
        Assert.That(added.Commands.Select(c => c.Id), Is.EqualTo(new[] { "demo.command" }));
        Assert.That(added.Revision, Is.Not.EqualTo(initial.Revision));
        Assert.That(addedUi.CapabilitiesRevision, Is.EqualTo(added.Revision));
        Assert.That(addedUi.Revision, Is.Not.EqualTo(initialUi.Revision));
        AssertError(await client.InvokeAsync("demo.command", new { }, initialUi.Revision, ct), ProtocolErrorCode.SchemaOutdated);
        AssertOutput(await client.InvokeAsync("demo.command", new { }, addedUi.Revision, ct), "first");

        gateway.Commands.Replace(CreateCommand("replacement"));
        var replaced = await GetCapabilities(client, ct);
        var replacedUi = await GetUi(client, ct);
        Assert.That(replaced.Revision, Is.Not.EqualTo(added.Revision));
        Assert.That(replacedUi.CapabilitiesRevision, Is.EqualTo(replaced.Revision));
        AssertError(await client.InvokeAsync("demo.command", new { }, addedUi.Revision, ct), ProtocolErrorCode.SchemaOutdated);
        AssertOutput(await client.InvokeAsync("demo.command", new { }, replacedUi.Revision, ct), "replacement");

        Assert.That(gateway.Commands.Remove("demo.command"), Is.True);
        var removed = await GetCapabilities(client, ct);
        var removedUi = await GetUi(client, ct);
        Assert.That(removed.Commands, Is.Empty);
        Assert.That(removed.Revision, Is.Not.EqualTo(replaced.Revision));
        Assert.That(removedUi.CapabilitiesRevision, Is.EqualTo(removed.Revision));
        AssertError(await client.InvokeAsync("demo.command", new { }, cancellationToken: ct), ProtocolErrorCode.CommandNotFound);
        Assert.That(gateway.Commands.Remove("demo.command"), Is.False);
        Assert.That((await GetCapabilities(client, ct)).Revision, Is.EqualTo(removed.Revision));
    }

    [Test]
    public async Task Availability_IsEvaluatedForCatalogAndAgainAtInvocation()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = timeout.Token;
        var available = true;
        var calls = 0;
        var policy = new DelegateCommandAvailability(() => Volatile.Read(ref available)
            ? new CommandAvailability(true) : new CommandAvailability(false, "Device disconnected"));
        var command = new GatewayCommand("demo.command", "Demo", null, Arguments, Result, (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return ValueTask.FromResult(Output("done"));
        }, policy);
        await using var gateway = CreateBuilder().AddCommand(command).Build(new TcpJsonServer(IPAddress.Loopback, 0));
        gateway.Start();
        await using var client = await Connect(gateway, ct);
        var enabledCatalog = await GetCapabilities(client, ct);
        var ui = await GetUi(client, ct);
        Assert.That(enabledCatalog.Commands.Single().Availability.Enabled, Is.True);
        Assert.That((await GetCapabilities(client, ct)).Revision, Is.EqualTo(enabledCatalog.Revision));

        Volatile.Write(ref available, false);
        var rejected = await client.InvokeAsync(command.Id, new { }, ui.Revision, ct);
        AssertError(rejected, ProtocolErrorCode.CommandUnavailable);
        Assert.That(rejected.GetProperty("error").GetProperty("message").GetString(), Is.EqualTo("Device disconnected"));
        Assert.That(calls, Is.Zero);
        var disabledCatalog = await GetCapabilities(client, ct);
        Assert.That(disabledCatalog.Commands.Single().Availability, Is.EqualTo(new CommandAvailability(false, "Device disconnected")));
        Assert.That(disabledCatalog.Revision, Is.Not.EqualTo(enabledCatalog.Revision));
        var disabledUi = await GetUi(client, ct);
        Assert.That(disabledUi.CapabilitiesRevision, Is.EqualTo(disabledCatalog.Revision));
        Assert.That(disabledUi.Revision, Is.EqualTo(ui.Revision));

        // Invocation must also re-evaluate a previously disabled catalog entry.
        Volatile.Write(ref available, true);
        AssertOutput(await client.InvokeAsync(command.Id, new { }, ui.Revision, ct), "done");
        Assert.That(calls, Is.EqualTo(1));
    }

    [Test]
    public async Task ReplacementAndRemoval_DoNotInterruptRunningHandler()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = timeout.Token;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new GatewayCommand("demo.command", "Demo", null, Arguments, Result, async (_, token) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(token);
            return Output("original");
        });
        await using var gateway = CreateBuilder().AddCommand(command).Build(new TcpJsonServer(IPAddress.Loopback, 0));
        gateway.Start();
        await using var firstClient = await Connect(gateway, ct);
        await using var secondClient = await Connect(gateway, ct);
        var running = firstClient.InvokeAsync(command.Id, new { }, cancellationToken: ct);
        try
        {
            await entered.Task.WaitAsync(ct);
            gateway.Commands.Replace(CreateCommand("replacement"));
            AssertOutput(await secondClient.InvokeAsync(command.Id, new { }, cancellationToken: ct), "replacement");
            gateway.Commands.Remove(command.Id);
            AssertError(await secondClient.InvokeAsync(command.Id, new { }, cancellationToken: ct), ProtocolErrorCode.CommandNotFound);
        }
        finally
        {
            release.TrySetResult();
        }
        AssertOutput(await running, "original");
    }

    [Test]
    public async Task AvailabilityFailure_DoesNotRunHandlerOrCloseConnection()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = timeout.Token;
        var calls = 0;
        var command = new GatewayCommand("demo.command", "Demo", null, Arguments, Result, (_, _) =>
        {
            calls++;
            return ValueTask.FromResult(Output("unexpected"));
        }, new DelegateCommandAvailability(() => throw new InvalidOperationException("Unavailable device")));
        await using var gateway = CreateBuilder().AddCommand(command).Build(new TcpJsonServer(IPAddress.Loopback, 0));
        gateway.Start();
        await using var client = await Connect(gateway, ct);
        var error = Assert.ThrowsAsync<GatewayRequestException>(async () =>
            await client.InvokeAsync(command.Id, new { }, cancellationToken: ct));
        Assert.That(error!.Error.Code, Is.EqualTo(ProtocolErrorCode.InternalError));
        Assert.That(calls, Is.Zero);
        gateway.Commands.Replace(CreateCommand("recovered"));
        AssertOutput(await client.InvokeAsync(command.Id, new { }, cancellationToken: ct), "recovered");
    }

    [Test]
    public void Registry_RejectsDuplicateAndMissingReplacementWithoutChangingSnapshot()
    {
        var registry = new GatewayCommandRegistry();
        var original = CreateCommand("original");
        registry.Add(original);
        var snapshot = registry.GetCommands();
        Assert.Throws<ArgumentException>(() => registry.Add(CreateCommand("duplicate")));
        Assert.That(registry.GetCommands().Single(), Is.SameAs(original));
        registry.Remove(original.Id);
        Assert.Throws<KeyNotFoundException>(() => registry.Replace(CreateCommand("missing")));
        Assert.That(registry.GetCommands(), Is.Empty);
        Assert.That(snapshot.Single(), Is.SameAs(original));
    }

    [Test]
    public void Command_ValidatesSchemaWithoutEvaluatingAvailability()
    {
        var evaluations = 0;
        var policy = new DelegateCommandAvailability(() =>
        {
            evaluations++;
            return new CommandAvailability(true);
        });
        Assert.Throws<InvalidRequestException>(() => new GatewayCommand("invalid", "Invalid", null,
            new ValueDescriptor { Type = ProtocolValueType.String }, Result,
            (_, _) => ValueTask.FromResult(Output("value")), policy));
        Assert.That(evaluations, Is.Zero);
    }

    [Test]
    public async Task BuilderAndBuiltServers_HaveIndependentRegistries()
    {
        var builder = CreateBuilder().AddCommand(CreateCommand("original"));
        await using var first = builder.Build(new TcpJsonServer(IPAddress.Loopback, 0));
        await using var second = builder.Build(new TcpJsonServer(IPAddress.Loopback, 0));
        first.Commands.Remove("demo.command");
        Assert.That(second.Commands.GetCommands(), Has.Count.EqualTo(1));
        await using var third = builder.Build(new TcpJsonServer(IPAddress.Loopback, 0));
        Assert.That(third.Commands.GetCommands(), Has.Count.EqualTo(1));
    }

    private static GatewayBuilder CreateBuilder() => new("test", "Test", "secret");
    private static GatewayCommand CreateCommand(string output) => new("demo.command", "Demo", null,
        Arguments, Result, (_, _) => ValueTask.FromResult(Output(output)));
    private static JsonElement Output(string value) => JsonSerializer.SerializeToElement(new { value });
    private static Task<NodeDeckClientConnection> Connect(GatewayServer gateway, CancellationToken ct) =>
        NodeDeckClientConnection.ConnectAsync("127.0.0.1", gateway.TcpPort!.Value, "secret", cancellationToken: ct);
    private static async Task<CapabilitiesDocument> GetCapabilities(NodeDeckClientConnection client, CancellationToken ct) =>
        (await client.GetCapabilitiesAsync(ct)).Deserialize<CapabilitiesDocument>(ProtocolJson.Options)!;
    private static async Task<UiDocument> GetUi(NodeDeckClientConnection client, CancellationToken ct) =>
        (await client.GetUiAsync(ct)).Deserialize<UiDocument>(ProtocolJson.Options)!;

    private static void AssertError(JsonElement result, string code)
    {
        Assert.That(result.GetProperty("status").GetString(), Is.EqualTo(ExecutionStatus.Rejected));
        Assert.That(result.GetProperty("error").GetProperty("code").GetString(), Is.EqualTo(code));
    }

    private static void AssertOutput(JsonElement result, string value)
    {
        Assert.That(result.GetProperty("status").GetString(), Is.EqualTo(ExecutionStatus.Completed));
        Assert.That(result.GetProperty("output").GetProperty("value").GetString(), Is.EqualTo(value));
    }
}
