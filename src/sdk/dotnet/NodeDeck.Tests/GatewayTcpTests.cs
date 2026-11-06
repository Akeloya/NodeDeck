using System.Net;
using System.Text.Json;
using NUnit.Framework;
using NodeDeck.Client;
using NodeDeck.Gateway;
using NodeDeck.Core.Protocol;
using NodeDeck.Transport.Tcp;
using ProtocolValueType = NodeDeck.Core.Protocol.ValueType;

namespace NodeDeck.Tests;

public class GatewayTcpTests
{
    private static readonly ValueDescriptor Arguments = new()
    {
        Type = ProtocolValueType.Object,
        Properties = new Dictionary<string, ValueDescriptor> { ["value"] = new() { Type = ProtocolValueType.String, MinLength = 1 } },
        Required = ["value"], AdditionalProperties = false
    };
    private static readonly ValueDescriptor Result = new()
    {
        Type = ProtocolValueType.Object,
        Properties = new Dictionary<string, ValueDescriptor> { ["echo"] = new() { Type = ProtocolValueType.String } },
        Required = ["echo"], AdditionalProperties = false
    };

    [Test]
    public async Task TcpConnection_RoundTripsJsonFrame()
    {
        await using var server = new TcpJsonServer(IPAddress.Loopback, 0);
        server.Start();
        var accepted = server.AcceptAsync();
        await using var client = await TcpJsonConnection.ConnectAsync("127.0.0.1", server.Port);
        await using var peer = await accepted;
        var sent = new { value = "hello" };
        await client.SendAsync(sent);
        await client.SendAsync(new { value = "second frame" });
        var received = await peer.ReceiveAsync<Dictionary<string, string>>();
        Assert.That(received!["value"], Is.EqualTo("hello"));
        var next = await peer.ReceiveAsync<Dictionary<string, string>>();
        Assert.That(next!["value"], Is.EqualTo("second frame"));
    }

    [Test]
    public async Task ClientAndGateway_AuthenticateAndReturnCommandResult()
    {
        await using var gateway = CreateGateway(out _);
        gateway.Start();
        await using var client = await NodeDeckClientConnection.ConnectAsync("127.0.0.1", gateway.TcpPort!.Value, "test-secret");
        var result = await client.InvokeAsync("demo.echo", new { value = "from phone" });
        Assert.That(result.GetProperty(ProtocolField.Status).GetString(), Is.EqualTo(ExecutionStatus.Completed));
        Assert.That(result.GetProperty(ProtocolField.Output).GetProperty("echo").GetString(), Is.EqualTo("from phone"));
    }

    [Test]
    public async Task Gateway_AcceptsClientsOnBothListeners()
    {
        var first = new TcpJsonServer(IPAddress.Loopback, 0);
        var second = new TcpJsonServer(IPAddress.Loopback, 0);
        await using var gateway = new GatewayBuilder("test-gateway", "Test Gateway", "test-secret")
            .BuildWithListeners([first, second]);
        gateway.Start();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var firstClient = await NodeDeckClientConnection.ConnectAsync(
            "127.0.0.1", first.Port, "test-secret", cancellationToken: timeout.Token);
        await using var secondClient = await NodeDeckClientConnection.ConnectAsync(
            "127.0.0.1", second.Port, "test-secret", cancellationToken: timeout.Token);
        var firstCapabilities = await firstClient.GetCapabilitiesAsync(timeout.Token);
        var secondCapabilities = await secondClient.GetCapabilitiesAsync(timeout.Token);
        Assert.That(JsonElement.DeepEquals(firstCapabilities, secondCapabilities), Is.True);
    }

    [Test]
    public async Task Gateway_RejectsInvalidArgumentsWithoutRunningHandler()
    {
        await using var gateway = CreateGateway(out var count);
        gateway.Start();
        await using var client = await NodeDeckClientConnection.ConnectAsync("127.0.0.1", gateway.TcpPort!.Value, "test-secret");
        var result = await client.InvokeAsync("demo.echo", new { unexpected = 1 });
        Assert.That(result.GetProperty(ProtocolField.Status).GetString(), Is.EqualTo(ExecutionStatus.Rejected));
        Assert.That(result.GetProperty(ProtocolField.Error).GetProperty(ProtocolField.Code).GetString(), Is.EqualTo(ProtocolErrorCode.InvalidArguments));
        Assert.That(count(), Is.EqualTo(0));
    }

    [Test]
    public async Task Gateway_DeduplicatesRetriedRequestId()
    {
        await using var gateway = CreateGateway(out var count);
        gateway.Start();
        await using var raw = await TcpJsonConnection.ConnectAsync("127.0.0.1", gateway.TcpPort!.Value, ProtocolLimits.Default.MaxMessageBytes);
        var hello = Request("hello", RequestType.ProtocolHello, new HelloPayload([ProtocolConstants.CurrentVersion], new ClientInfo("test", "test")));
        await raw.SendAsync(hello);
        _ = await raw.ReceiveAsync<ProtocolResponse>();
        var login = Request("login", RequestType.AuthLogin, new LoginPayload(AuthMethod.SharedSecret, "test-secret"));
        await raw.SendAsync(login);
        var loginResponse = await raw.ReceiveAsync<ProtocolResponse>();
        var token = loginResponse!.Result!.Value.GetProperty(ProtocolField.AccessToken).GetString();
        var payload = JsonSerializer.SerializeToElement(new CommandInvokePayload("demo.echo", JsonSerializer.SerializeToElement(new { value = "once" })), ProtocolJson.Options);
        var invoke = new ProtocolRequest(ProtocolConstants.CurrentVersion, "same-id", RequestType.CommandInvoke, payload, token);
        await raw.SendAsync(invoke);
        var first = await raw.ReceiveAsync<ProtocolResponse>();
        // A retry keeps its original outcome even after the command has been removed.
        gateway.Commands.Remove("demo.echo");
        await raw.SendAsync(invoke);
        var second = await raw.ReceiveAsync<ProtocolResponse>();
        Assert.That(second!.Result!.Value.GetProperty(ProtocolField.Output).GetProperty("echo").GetString(), Is.EqualTo(first!.Result!.Value.GetProperty(ProtocolField.Output).GetProperty("echo").GetString()));
        Assert.That(count(), Is.EqualTo(1));
    }

    private static GatewayServer CreateGateway(out Func<int> count)
    {
        var executions = 0;
        count = () => Volatile.Read(ref executions);
        return new GatewayBuilder("test-gateway", "Test Gateway", "test-secret")
            .AddCommand(new GatewayCommand("demo.echo", "Echo", null, Arguments, Result, (arguments, _) =>
            {
                Interlocked.Increment(ref executions);
                return ValueTask.FromResult(JsonSerializer.SerializeToElement(new { echo = arguments.GetProperty("value").GetString() }));
            })).Build(new TcpJsonServer(new TcpListenerSettings { IPAddress = "127.0.0.1", Port = 0 }));
    }

    private static ProtocolRequest Request<T>(string id, string type, T payload) =>
        new(ProtocolConstants.CurrentVersion, id, type, JsonSerializer.SerializeToElement(payload, ProtocolJson.Options));
}
