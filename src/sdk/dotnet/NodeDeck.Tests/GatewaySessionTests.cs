using System.Net;
using System.Text.Json;
using NodeDeck.Client;
using NodeDeck.Core.Protocol;
using NodeDeck.Gateway;
using NodeDeck.Transport.Tcp;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;

namespace NodeDeck.Tests;

public class GatewaySessionTests
{
    private static GatewayBuilder Builder(GatewaySessionOptions? options = null) =>
        new GatewayBuilder("gateway", "Gateway", "secret").WithSessions(options ?? new());

    private static TcpJsonServer Listener() => new(IPAddress.Loopback, 0);

    [Test]
    public void Defaults_UseFiveMinuteTimeoutAndMinuteIntervals()
    {
        var options = new GatewaySessionOptions();
        Assert.That(options.InactivityTimeout, Is.EqualTo(TimeSpan.FromMinutes(5)));
        Assert.That(options.PingInterval, Is.EqualTo(TimeSpan.FromMinutes(1)));
        Assert.That(options.ActiveUsersLogInterval, Is.EqualTo(TimeSpan.FromMinutes(1)));
    }

    [Test]
    public async Task SameDeviceOnAnotherListener_RevokesOldSessionButKeepsOtherDevices()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var first = Listener();
        var second = Listener();
        await using var gateway = Builder().BuildWithListeners([first, second]);
        gateway.Start();
        await using var old = await Connect(first, "phone", deadline.Token);
        await using var other = await Connect(first, "other-phone", deadline.Token);
        await using var replacement = await Connect(second, "phone", deadline.Token);
        Assert.That(gateway.ActiveSessions.Select(s => s.DeviceId), Is.EquivalentTo(new[] { "phone", "other-phone" }));
        var error = Assert.ThrowsAsync<GatewayRequestException>(async () => await old.InvokeAsync("anything", new { }, cancellationToken: deadline.Token));
        Assert.That(error!.Code, Is.EqualTo(ProtocolErrorCode.Unauthorized));
        await old.DisposeAsync();
        await replacement.GetCapabilitiesAsync(deadline.Token);
        await other.GetCapabilitiesAsync(deadline.Token);
        Assert.That(gateway.ActiveSessions, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task FailedLogin_DoesNotEvictAnAuthenticatedDevice()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = Listener();
        await using var gateway = Builder().Build(listener);
        gateway.Start();
        await using var client = await Connect(listener, "phone", deadline.Token);
        Assert.ThrowsAsync<GatewayRequestException>(async () => await NodeDeckClientConnection.ConnectAsync(
            "127.0.0.1", listener.Port, "wrong", "phone", cancellationToken: deadline.Token));
        await client.GetCapabilitiesAsync(deadline.Token);
        Assert.That(gateway.ActiveSessions.Single().DeviceId, Is.EqualTo("phone"));
    }

    [Test]
    public async Task EndSession_ReturnsUnauthorizedAndAllowsFreshConnection()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = Listener();
        await using var gateway = Builder().Build(listener);
        gateway.Start();
        await using var client = await Connect(listener, "phone", deadline.Token);
        Assert.That(gateway.EndSession(gateway.ActiveSessions.Single().SessionId), Is.True);
        var error = Assert.ThrowsAsync<GatewayRequestException>(async () => await client.GetCapabilitiesAsync(deadline.Token));
        Assert.That(error!.Code, Is.EqualTo(ProtocolErrorCode.Unauthorized));
        await using var replacement = await Connect(listener, "phone", deadline.Token);
        await replacement.GetCapabilitiesAsync(deadline.Token);
    }

    [Test]
    public async Task Logout_ClosesTransportAndCanBeRepeated()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = Listener();
        await using var gateway = Builder().Build(listener);
        gateway.Start();
        for (var i = 0; i < 3; i++)
        {
            await using var raw = await TcpJsonConnection.ConnectAsync("127.0.0.1", listener.Port, cancellationToken: deadline.Token);
            var token = await Login(raw, "phone", deadline.Token);
            await raw.SendAsync(Request(RequestType.AuthLogout, new { }, token), deadline.Token);
            var response = await raw.ReceiveAsync<ProtocolResponse>(deadline.Token);
            Assert.That(response!.Error, Is.Null);
            Assert.That(await raw.ReceiveAsync<ProtocolResponse>(deadline.Token), Is.Null);
            Assert.That(gateway.ActiveSessions, Is.Empty);
        }
    }

    [Test]
    public async Task AbruptDisconnect_RemovesSession()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = Listener();
        await using var gateway = Builder().Build(listener);
        gateway.Start();
        var raw = await TcpJsonConnection.ConnectAsync("127.0.0.1", listener.Port, cancellationToken: deadline.Token);
        await Login(raw, "phone", deadline.Token);
        await raw.DisposeAsync();
        await Until(() => gateway.ActiveSessions.Count == 0, deadline.Token);
    }

    [Test]
    public async Task MissingOrUncorrelatedPong_ExpiresSessionAndCannotReviveIt()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = Listener();
        await using var gateway = Builder(new GatewaySessionOptions
        {
            PingInterval = TimeSpan.FromMilliseconds(50), InactivityTimeout = TimeSpan.FromMilliseconds(400)
        }).Build(listener);
        gateway.Start();
        await using var raw = await TcpJsonConnection.ConnectAsync("127.0.0.1", listener.Port, cancellationToken: deadline.Token);
        var token = await Login(raw, "silent-phone", deadline.Token);
        var ping = await raw.ReceiveAsync<ProtocolRequest>(deadline.Token);
        Assert.That(ping!.Type, Is.EqualTo(RequestType.SessionPing));
        await raw.SendAsync(ProtocolResponse.Success(ping with { RequestId = "wrong-id" }, new { }), deadline.Token);
        await Until(() => gateway.ActiveSessions.Count == 0, deadline.Token);
        await raw.SendAsync(ProtocolResponse.Success(ping, new { }), deadline.Token);
        var command = Request(RequestType.CommandInvoke, new CommandInvokePayload("anything", JsonSerializer.SerializeToElement(new { })), token);
        await raw.SendAsync(command, deadline.Token);
        ProtocolResponse? response;
        do { response = await raw.ReceiveAsync<ProtocolResponse>(deadline.Token); }
        while (response!.Type == RequestType.SessionPing);
        Assert.That(response.Error!.Code, Is.EqualTo(ProtocolErrorCode.Unauthorized));
        Assert.That(gateway.ActiveSessions, Is.Empty);
    }

    [Test]
    public async Task PingContinuesDuringLongCommand_AndWhileIdle()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = Listener();
        var descriptor = new ValueDescriptor { Type = NodeDeck.Core.Protocol.ValueType.Object, Properties = new Dictionary<string, ValueDescriptor>(), Required = [], AdditionalProperties = false };
        await using var gateway = Builder(new GatewaySessionOptions
        {
            PingInterval = TimeSpan.FromMilliseconds(50), InactivityTimeout = TimeSpan.FromMilliseconds(600)
        }).AddCommand(new GatewayCommand("slow", "Slow", null, descriptor, descriptor, async (_, ct) =>
        {
            await Task.Delay(1200, ct);
            return JsonSerializer.SerializeToElement(new { });
        })).Build(listener);
        gateway.Start();
        await using var client = await Connect(listener, "phone", deadline.Token);
        var initial = gateway.ActiveSessions.Single();
        var result = await client.InvokeAsync("slow", new { }, cancellationToken: deadline.Token);
        Assert.That(result.GetProperty("status").GetString(), Is.EqualTo(ExecutionStatus.Completed));
        await Task.Delay(800, deadline.Token);
        Assert.That(gateway.ActiveSessions.Single().LastSeenAt, Is.GreaterThan(initial.LastSeenAt));
        await client.GetCapabilitiesAsync(deadline.Token);
    }

    [Test]
    public async Task WrongGatewayIdentity_IsRejectedBeforeLogin()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = Listener();
        await using var gateway = Builder().Build(listener);
        gateway.Start();
        Assert.ThrowsAsync<InvalidResponseException>(async () => await NodeDeckClientConnection.ConnectAsync(
            "127.0.0.1", listener.Port, "secret", cancellationToken: deadline.Token, expectedGatewayId: "another-gateway"));
        Assert.That(gateway.ActiveSessions, Is.Empty);
    }

    [Test]
    public async Task ActiveUsersLog_IncludesIdentityButNotCredentials()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var previous = LogManager.Configuration;
        var target = new MemoryTarget { Layout = "${message}" };
        var configuration = new LoggingConfiguration();
        configuration.AddRule(LogLevel.Info, LogLevel.Fatal, target, "NodeDeck.Gateway.GatewayServer");
        LogManager.Configuration = configuration;
        try
        {
            var listener = Listener();
            await using var gateway = Builder(new GatewaySessionOptions
            {
                ActiveUsersLogInterval = TimeSpan.FromMilliseconds(50)
            }).Build(listener);
            gateway.Start();
            await using var client = await Connect(listener, "logged-phone", deadline.Token);
            await Until(() => target.Logs.Any(line => line.Contains("logged-phone")), deadline.Token);
            Assert.That(string.Join("\n", target.Logs), Does.Not.Contain("accessToken").And.Not.Contain("secret"));
            await client.DisposeAsync();
            await Until(() => target.Logs.Any(line => line.Contains("Active users (0)")), deadline.Token);
        }
        finally { LogManager.Configuration = previous; }
    }

    [Test]
    public void BleIdentifiers_RejectEmptyOrOverlappingValues()
    {
        new BleServiceIdentifiers().Validate();
        Assert.Throws<ArgumentException>(() => new BleServiceIdentifiers { ServiceUuid = Guid.Empty }.Validate());
        Assert.Throws<ArgumentException>(() => new BleServiceIdentifiers
        {
            ServiceUuid = Guid.Parse(BleProtocolConstants.ClientToGatewayCharacteristicUuid)
        }.Validate());
    }

    private static Task<NodeDeckClientConnection> Connect(TcpJsonServer listener, string deviceId, CancellationToken token) =>
        NodeDeckClientConnection.ConnectAsync("127.0.0.1", listener.Port, "secret", deviceId, cancellationToken: token);

    private static async Task<string> Login(TcpJsonConnection raw, string deviceId, CancellationToken ct)
    {
        await raw.SendAsync(Request(RequestType.ProtocolHello, new HelloPayload([ProtocolConstants.CurrentVersion], new ClientInfo(deviceId, "Test phone"))), ct);
        Assert.That((await raw.ReceiveAsync<ProtocolResponse>(ct))!.Error, Is.Null);
        raw.SetMaxMessageBytes(ProtocolLimits.Default.MaxMessageBytes);
        await raw.SendAsync(Request(RequestType.AuthLogin, new LoginPayload(AuthMethod.SharedSecret, "secret")), ct);
        return (await raw.ReceiveAsync<ProtocolResponse>(ct))!.Result!.Value.Deserialize<LoginResult>(ProtocolJson.Options)!.AccessToken;
    }

    private static ProtocolRequest Request(string type, object payload, string? token = null) =>
        new(ProtocolConstants.CurrentVersion, Guid.NewGuid().ToString("N"), type, JsonSerializer.SerializeToElement(payload, ProtocolJson.Options), token);

    private static async Task Until(Func<bool> predicate, CancellationToken token)
    {
        while (!predicate())
            await Task.Delay(20, token);
    }
}
