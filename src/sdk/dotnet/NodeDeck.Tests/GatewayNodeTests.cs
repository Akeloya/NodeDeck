using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using NodeDeck.Client;
using NodeDeck.Core.Protocol;
using NodeDeck.Core.Transport;
using NodeDeck.Gateway;
using NodeDeck.Transport.Tcp;
using NUnit.Framework;

namespace NodeDeck.Tests;

public class GatewayNodeTests
{
    private static readonly ValueDescriptor Empty = new()
    {
        Type = NodeDeck.Core.Protocol.ValueType.Object,
        Properties = new Dictionary<string, ValueDescriptor>(), Required = [], AdditionalProperties = false
    };

    [Test]
    public async Task Factory_CreatesBothRolesThroughInterface_AndPublishesRuntimeMetadata()
    {
        using var deadline = Deadline();
        await using var executor = await Create("executor", GatewayMode.Executor);
        await using var node = await Create("node", GatewayMode.Node, [executor.Endpoint]);
        await node.Gateway.Ready.WaitAsync(deadline.Token);
        Assert.That(executor.Gateway.Info.Kind, Is.EqualTo("executor"));
        Assert.That(node.Gateway.Info.Kind, Is.EqualTo("node"));
        await using var phone = await node.Connect("phone", deadline.Token);
        var first = await Catalog(phone, deadline.Token);
        var connect = first.Commands.Single();
        Assert.That(connect.Title, Is.EqualTo("executor name"));
        Assert.That(connect.Description, Is.EqualTo("executor description"));
        Assert.That(connect.Availability.Enabled, Is.True);
        executor.Gateway.UpdateMetadata("Renamed executor", "Runtime description");
        await Until(() => node.Gateway.ConnectedGateways.SingleOrDefault()?.Gateway.Name == "Renamed executor", deadline.Token);
        var changed = await Catalog(phone, deadline.Token);
        Assert.That(changed.Revision, Is.Not.EqualTo(first.Revision));
        Assert.That(changed.Commands.Single().Title, Is.EqualTo("Renamed executor"));
        Assert.That(changed.Commands.Single().Description, Is.EqualTo("Runtime description"));
        node.Gateway.UpdateMetadata("Renamed node", "Node runtime description");
        Assert.That((await Catalog(phone, deadline.Token)).Gateway.Name, Is.EqualTo("Renamed node"));
    }

    [Test]
    public async Task Selection_IsPerPhone_AndIdenticalCommandIdsExecuteOnlyOnSelectedGateway()
    {
        using var deadline = Deadline();
        var firstCount = 0;
        var secondCount = 0;
        await using var first = await Create("first", GatewayMode.Executor, configure: commands => AddCommand(commands, () => firstCount++));
        await using var second = await Create("second", GatewayMode.Executor, configure: commands => AddCommand(commands, () => secondCount++));
        await using var node = await Create("node", GatewayMode.Node, [first.Endpoint, second.Endpoint]);
        await node.Gateway.Ready.WaitAsync(deadline.Token);
        await using var one = await node.Connect("phone-one", deadline.Token);
        await using var two = await node.Connect("phone-two", deadline.Token);
        Assert.That((await Catalog(one, deadline.Token)).Commands, Has.Count.EqualTo(2));
        await Select(one, "node", "first", deadline.Token);
        await Select(two, "node", "second", deadline.Token);
        Assert.That((await Catalog(one, deadline.Token)).Gateway.Id, Is.EqualTo("first"));
        Assert.That((await Catalog(two, deadline.Token)).Gateway.Id, Is.EqualTo("second"));
        await one.InvokeAsync("execute", new { }, cancellationToken: deadline.Token);
        Assert.That((firstCount, secondCount), Is.EqualTo((1, 0)));
        await two.InvokeAsync("execute", new { }, cancellationToken: deadline.Token);
        Assert.That((firstCount, secondCount), Is.EqualTo((1, 1)));
        await one.InvokeAsync(GatewayNode.DisconnectCommandId("node"), new { }, cancellationToken: deadline.Token);
        Assert.That((await Catalog(one, deadline.Token)).Gateway.Id, Is.EqualTo("node"));
        Assert.That((await Catalog(two, deadline.Token)).Gateway.Id, Is.EqualTo("second"));
        await Until(() => first.Gateway.ActiveSessions.All(session => session.DeviceId != "phone-one"), deadline.Token);
    }

    [Test]
    public async Task SelectedGatewayFailure_DoesNotBroadcastRetryOrFallBack_AndDirectoryRemainsReachable()
    {
        using var deadline = Deadline();
        var otherCount = 0;
        await using var executor = await Create("executor", GatewayMode.Executor, configure: commands => AddCommand(commands, () => { }));
        await using var other = await Create("other", GatewayMode.Executor, configure: commands => AddCommand(commands, () => otherCount++));
        await using var node = await Create("node", GatewayMode.Node, [executor.Endpoint, other.Endpoint]);
        await node.Gateway.Ready.WaitAsync(deadline.Token);
        await using var phone = await node.Connect("phone", deadline.Token);
        await Select(phone, "node", "executor", deadline.Token);
        await executor.DisposeAsync();
        await Until(() => node.Gateway.ConnectedGateways.All(peer => peer.Gateway.Id != "executor"), deadline.Token);
        var result = (await phone.InvokeAsync("execute", new { }, cancellationToken: deadline.Token))
            .Deserialize<CommandExecutionResult>(ProtocolJson.Options)!;
        Assert.That(result.Error!.Code, Is.EqualTo(ProtocolErrorCode.GatewayUnavailable));
        Assert.That(otherCount, Is.Zero);
        var catalog = await Catalog(phone, deadline.Token);
        Assert.That(catalog.Commands.Single(command => command.Id == "execute").Availability.Enabled, Is.False);
        Assert.That(catalog.Commands.Single(command => command.Id == GatewayNode.DisconnectCommandId("node")).Availability.Enabled, Is.True);
        await phone.InvokeAsync(GatewayNode.DisconnectCommandId("node"), new { }, cancellationToken: deadline.Token);
        var directory = await Catalog(phone, deadline.Token);
        Assert.That(directory.Commands.Single(command => command.Id == GatewayNode.ConnectCommandId("node", "executor")).Availability.Enabled, Is.False);
    }

    [Test]
    public async Task CyclicTopology_RejectsBackwardSelectionWithoutEvictingOriginalSession()
    {
        using var deadline = Deadline();
        var portA = FreePort();
        var portB = FreePort();
        await using var a = await Create("a", GatewayMode.Node, [Endpoint("b", portB)], port: portA);
        await using var b = await Create("b", GatewayMode.Node, [Endpoint("a", portA)], port: portB);
        await Until(() => a.Gateway.ConnectedGateways.Count == 1 && b.Gateway.ConnectedGateways.Count == 1, deadline.Token);
        await using var phone = await a.Connect("phone", deadline.Token);
        await Select(phone, "a", "b", deadline.Token);
        var directory = await Catalog(phone, deadline.Token);
        Assert.That(directory.Commands.Single(command => command.Id == GatewayNode.ConnectCommandId("b", "a")).Availability.Enabled, Is.False);
        var error = Assert.ThrowsAsync<GatewayRequestException>(async () =>
            await phone.InvokeAsync(GatewayNode.ConnectCommandId("b", "a"), new { }, cancellationToken: deadline.Token));
        Assert.That(error!.Code, Is.EqualTo(ProtocolErrorCode.RouteLoop));
        Assert.That(a.Gateway.ActiveSessions.Count(session => session.DeviceId == "phone"), Is.EqualTo(1));
        Assert.That((await Catalog(phone, deadline.Token)).Gateway.Id, Is.EqualTo("b"));
        await phone.InvokeAsync(GatewayNode.DisconnectCommandId("a"), new { }, cancellationToken: deadline.Token);
        Assert.That((await Catalog(phone, deadline.Token)).Gateway.Id, Is.EqualTo("a"));
    }

    [Test]
    public async Task NestedNodes_ForwardOriginalRequestId_AndCannotExceedHopBudget()
    {
        using var deadline = Deadline();
        var executions = 0;
        await using var executor = await Create("executor", GatewayMode.Executor, configure: commands => AddCommand(commands, () => executions++));
        await using var b = await Create("b", GatewayMode.Node, [executor.Endpoint]);
        await using var a = await Create("a", GatewayMode.Node, [b.Endpoint]);
        await Task.WhenAll(a.Gateway.Ready, b.Gateway.Ready).WaitAsync(deadline.Token);
        await using var phone = await a.Connect("phone", deadline.Token);
        await Select(phone, "a", "b", deadline.Token);
        await Select(phone, "b", "executor", deadline.Token);
        Assert.That((await Catalog(phone, deadline.Token)).Gateway.Id, Is.EqualTo("executor"));
        var request = new ProtocolRequest(ProtocolConstants.CurrentVersion, "same-command", RequestType.CommandInvoke,
            JsonSerializer.SerializeToElement(new CommandInvokePayload("execute", JsonSerializer.SerializeToElement(new { })), ProtocolJson.Options));
        await phone.ForwardAsync(request, GatewayRoute.Empty, deadline.Token);
        await phone.ForwardAsync(request, GatewayRoute.Empty, deadline.Token);
        Assert.That(executions, Is.EqualTo(1));
        var error = Assert.ThrowsAsync<GatewayRequestException>(async () =>
            await phone.ForwardAsync(request with { RequestId = "limited" }, new GatewayRoute([], 1), deadline.Token));
        Assert.That(error!.Code, Is.EqualTo(ProtocolErrorCode.RouteHopLimit));
        Assert.That(executions, Is.EqualTo(1));
    }

    [Test]
    public async Task SelectionRequestId_CannotBeReusedForAnExecutorCommand()
    {
        using var deadline = Deadline();
        var executions = 0;
        await using var executor = await Create("executor", GatewayMode.Executor, configure: commands => AddCommand(commands, () => executions++));
        await using var node = await Create("node", GatewayMode.Node, [executor.Endpoint]);
        await node.Gateway.Ready.WaitAsync(deadline.Token);
        await using var phone = await node.Connect("phone", deadline.Token);
        var request = new ProtocolRequest(ProtocolConstants.CurrentVersion, "selection-id", RequestType.CommandInvoke,
            JsonSerializer.SerializeToElement(new CommandInvokePayload(GatewayNode.ConnectCommandId("node", "executor"), JsonSerializer.SerializeToElement(new { })), ProtocolJson.Options));
        await phone.ForwardAsync(request, GatewayRoute.Empty, deadline.Token);
        var error = Assert.ThrowsAsync<GatewayRequestException>(async () => await phone.ForwardAsync(request with
        {
            Payload = JsonSerializer.SerializeToElement(new CommandInvokePayload("execute", JsonSerializer.SerializeToElement(new { })), ProtocolJson.Options)
        }, GatewayRoute.Empty, deadline.Token));
        Assert.That(error!.Code, Is.EqualTo(ProtocolErrorCode.RequestConflict));
        Assert.That(executions, Is.Zero);
    }

    [Test]
    public async Task Executor_RejectsAnIncomingRouteContainingItsOwnId()
    {
        using var deadline = Deadline();
        var executions = 0;
        await using var executor = await Create("executor", GatewayMode.Executor, configure: commands => AddCommand(commands, () => executions++));
        await using var phone = await executor.Connect("phone", deadline.Token);
        var request = new ProtocolRequest(ProtocolConstants.CurrentVersion, "loop", RequestType.CommandInvoke,
            JsonSerializer.SerializeToElement(new CommandInvokePayload("execute", JsonSerializer.SerializeToElement(new { })), ProtocolJson.Options));
        var error = Assert.ThrowsAsync<GatewayRequestException>(async () => await phone.ForwardAsync(request,
            new GatewayRoute(["executor"], 15), deadline.Token));
        Assert.That(error!.Code, Is.EqualTo(ProtocolErrorCode.RouteLoop));
        Assert.That(executions, Is.Zero);
    }

    [Test]
    public async Task RevokingNodeSession_AlsoReleasesExecutorSession()
    {
        using var deadline = Deadline();
        await using var executor = await Create("executor", GatewayMode.Executor);
        await using var node = await Create("node", GatewayMode.Node, [executor.Endpoint]);
        await node.Gateway.Ready.WaitAsync(deadline.Token);
        await using var phone = await node.Connect("phone", deadline.Token);
        await Select(phone, "node", "executor", deadline.Token);
        Assert.That(executor.Gateway.ActiveSessions.Any(session => session.DeviceId == "phone"), Is.True);
        node.Gateway.EndSession(node.Gateway.ActiveSessions.Single(session => session.DeviceId == "phone").SessionId);
        await Until(() => executor.Gateway.ActiveSessions.All(session => session.DeviceId != "phone"), deadline.Token);
        var error = Assert.ThrowsAsync<GatewayRequestException>(async () => await phone.GetCapabilitiesAsync(deadline.Token));
        Assert.That(error!.Code, Is.EqualTo(ProtocolErrorCode.Unauthorized));
    }

    [Test]
    public async Task Factory_LoadsCompleteSettingsAndRejectsSelfRoutesAndUnknownModes()
    {
        var listeners = new ProtocolMessageListenerFactory().Register<TcpListenerSettings>(settings => new TcpJsonServer(settings));
        var factory = new GatewayFactory(listeners);
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, """
                {"Mode":"Node","GatewayId":"node","Name":"Directory","Description":"Choose gateway","SharedSecret":"secret",
                 "Listeners":[{"Type":"tcp","IPAddress":"127.0.0.1","Port":0}],"Gateways":[]}
                """);
            var settings = factory.LoadSettings(path);
            await using IGateway gateway = await factory.CreateAsync(settings);
            Assert.That(gateway.Info.Description, Is.EqualTo("Choose gateway"));
            Assert.ThrowsAsync<ArgumentException>(async () => await factory.CreateAsync(settings with { Gateways = [Endpoint("node", 5050)] }));
            Assert.ThrowsAsync<ArgumentException>(async () => await factory.CreateAsync(settings with { Mode = (GatewayMode)100 }));
            Assert.ThrowsAsync<ArgumentException>(async () => await factory.CreateAsync(settings, _ => { }));
        }
        finally { File.Delete(path); }
    }

    private static async Task Select(NodeDeckClientConnection phone, string node, string gateway, CancellationToken token)
    {
        var result = (await phone.InvokeAsync(GatewayNode.ConnectCommandId(node, gateway), new { }, cancellationToken: token))
            .Deserialize<CommandExecutionResult>(ProtocolJson.Options)!;
        Assert.That(result.Status, Is.EqualTo(ExecutionStatus.Completed), result.Error?.Message);
        Assert.That(result.RefreshCapabilities, Is.True);
    }

    private static async Task<CapabilitiesDocument> Catalog(NodeDeckClientConnection phone, CancellationToken token)
    {
        var catalog = (await phone.GetCapabilitiesAsync(token)).Deserialize<CapabilitiesDocument>(ProtocolJson.Options)!;
        ProtocolValidator.ValidateCapabilities(catalog);
        return catalog;
    }

    private static void AddCommand(GatewayCommandRegistry commands, Action action) => commands.Add(new GatewayCommand(
        "execute", "Execute", "Execute on this gateway", Empty, Empty, (_, _) =>
        {
            action();
            return ValueTask.FromResult(JsonSerializer.SerializeToElement(new { }));
        }));

    private static GatewayEndpointSettings Endpoint(string id, int port) => new()
    {
        GatewayId = id, Name = "Configured " + id, Description = "Configured description", Host = "127.0.0.1", Port = port, SharedSecret = "secret"
    };

    private static async Task<Host> Create(string id, GatewayMode mode, GatewayEndpointSettings[]? peers = null,
        Action<GatewayCommandRegistry>? configure = null, int port = 0)
    {
        TcpJsonServer? listener = null;
        var transports = new ProtocolMessageListenerFactory().Register<TcpListenerSettings>(settings => listener = new TcpJsonServer(settings));
        var settings = new GatewaySettings
        {
            Mode = mode, GatewayId = id, Name = id + " name", Description = id + " description", SharedSecret = "secret",
            Listeners = [transports.SerializeSettings(new TcpListenerSettings { IPAddress = "127.0.0.1", Port = port })],
            Gateways = peers ?? [], CatalogRefreshInterval = TimeSpan.FromMilliseconds(100), RouteTimeout = TimeSpan.FromSeconds(2)
        };
        IGateway gateway = await new GatewayFactory(transports).CreateAsync(settings, configure);
        gateway.Start();
        return new Host(gateway, listener!.Port);
    }

    private sealed class Host(IGateway gateway, int port) : IAsyncDisposable
    {
        private int _disposed;
        public IGateway Gateway { get; } = gateway;
        public GatewayEndpointSettings Endpoint => GatewayNodeTests.Endpoint(Gateway.Info.Id, port);
        public Task<NodeDeckClientConnection> Connect(string deviceId, CancellationToken token) =>
            NodeDeckClientConnection.ConnectAsync("127.0.0.1", port, "secret", deviceId, cancellationToken: token);
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                await Gateway.DisposeAsync();
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static CancellationTokenSource Deadline() => new(TimeSpan.FromSeconds(15));
    private static async Task Until(Func<bool> condition, CancellationToken token)
    {
        while (!condition())
            await Task.Delay(20, token);
    }
}
