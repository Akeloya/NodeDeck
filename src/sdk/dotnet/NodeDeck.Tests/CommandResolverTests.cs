using System.Net;
using System.Text.Json;
using NodeDeck.Client;
using NodeDeck.Core.Protocol;
using NodeDeck.Gateway;
using NodeDeck.Transport.Tcp;
using NUnit.Framework;
using ProtocolValueType = NodeDeck.Core.Protocol.ValueType;

namespace NodeDeck.Tests;

public class CommandResolverTests
{
    [Test]
    public async Task Resolve_BindsMetadataArgumentsDefaultsAndReturnSchema()
    {
        var registry = new GatewayCommandRegistry();
        var owner = new TypedCommands();
        var commands = new CommandResolver(registry).Resolve(owner);
        var command = commands.Single(c => c.Id == "sum");
        Assert.That(command.Title, Is.EqualTo("Sum"));
        Assert.That(command.Description, Is.EqualTo("Add two integers"));
        Assert.That(command.Parameters.Required, Is.EqualTo(new[] { "first" }));
        Assert.That(command.Parameters.Properties!.Keys, Is.EquivalentTo(new[] { "first", "second" }));
        Assert.That(command.Parameters.Properties["first"].Type, Is.EqualTo(ProtocolValueType.Integer));
        Assert.That(command.Parameters.Properties["first"].Maximum, Is.EqualTo(int.MaxValue));
        Assert.That(command.Result.Properties!["value"].Type, Is.EqualTo(ProtocolValueType.Integer));
        Assert.That(registry.GetCommands(), Has.Count.EqualTo(commands.Count));
        var output = await command.Handler(JsonSerializer.SerializeToElement(new { first = 3 }), default);
        Assert.That(output.GetProperty("value").GetInt32(), Is.EqualTo(5));
        Assert.That(ProtocolValidator.ValidateArguments(command.Result, output), Is.Empty);
    }

    [TestCase("sync")]
    [TestCase("task")]
    [TestCase("valueTask")]
    [TestCase("taskResult")]
    [TestCase("valueTaskResult")]
    public async Task Resolve_AwaitsSupportedMethodsAndInjectsCancellation(string id)
    {
        var owner = new AsyncCommands();
        var command = new CommandResolver(new()).Resolve(owner).Single(c => c.Id == id);
        using var cancellation = new CancellationTokenSource();
        var result = await command.Handler(JsonSerializer.SerializeToElement(new { }), cancellation.Token);
        Assert.That(owner.Completed, Is.True);
        Assert.That(owner.Token, Is.EqualTo(cancellation.Token));
        Assert.That(command.Parameters.Properties, Is.Empty);
        Assert.That(ProtocolValidator.ValidateArguments(command.Result, result), Is.Empty);
        if (id.EndsWith("Result", StringComparison.Ordinal))
            Assert.That(result.GetProperty("value").GetString(), Is.EqualTo("done"));
        else
            Assert.That(result.EnumerateObject().Count(), Is.Zero);
    }

    [Test]
    public async Task Resolve_ConvertsArraysAndPrimitiveArguments()
    {
        var command = new CommandResolver(new()).Resolve(new TypedCommands()).Single(c => c.Id == "format");
        var input = JsonSerializer.SerializeToElement(new { prefix = "value", enabled = true, values = new[] { 1.5, 2.5 } });
        Assert.That(ProtocolValidator.ValidateArguments(command.Parameters, input), Is.Empty);
        var result = await command.Handler(input, default);
        Assert.That(result.GetProperty("value").EnumerateArray().Select(item => item.GetString()),
            Is.EqualTo(new[] { "value:True:2" }));
        Assert.That(ProtocolValidator.ValidateArguments(command.Result, result), Is.Empty);
    }

    [Test]
    public void Resolve_UsesConventionAndExplicitPropertiesMethodsAndDelegateProperties()
    {
        var owner = new AvailabilityCommands();
        var commands = new CommandResolver(new()).Resolve(owner);
        Assert.That(owner.Reads, Is.Zero, "Resolving metadata must not evaluate availability.");
        foreach (var command in commands)
            Assert.That(command.Availability.GetAvailability(), Is.EqualTo(new CommandAvailability(false, "Not ready")));
        owner.Ready = true;
        foreach (var command in commands)
            Assert.That(command.Availability.GetAvailability().Enabled, Is.True, command.Id);
        owner.Check = () => false;
        Assert.That(commands.Single(c => c.Id == "delegate").Availability.GetAvailability().Enabled, Is.False);
    }

    [Test]
    public async Task Resolve_InheritsAttributesAndBindsOverridesToTheOwner()
    {
        var command = new CommandResolver(new()).Resolve(new DerivedCommands()).Single();
        var result = await command.Handler(JsonSerializer.SerializeToElement(new { }), default);
        Assert.That(result.GetProperty("value").GetString(), Is.EqualTo("derived"));
        Assert.That(command.Availability.GetAvailability().Enabled, Is.True);
    }

    [TestCase(typeof(InvalidAvailability))]
    [TestCase(typeof(MissingAvailability))]
    [TestCase(typeof(UnsupportedSignature))]
    [TestCase(typeof(AsyncVoidCommand))]
    [TestCase(typeof(StaticCommand))]
    [TestCase(typeof(GenericCommand))]
    [TestCase(typeof(RefCommand))]
    [TestCase(typeof(NullDefaultCommand))]
    [TestCase(typeof(DuplicateCommands))]
    public void Resolve_InvalidDeclarationsDoNotPartiallyRegister(Type ownerType)
    {
        var registry = new GatewayCommandRegistry();
        var error = Assert.Throws<ArgumentException>(() => new CommandResolver(registry).Resolve(Activator.CreateInstance(ownerType)!));
        Assert.That(error!.Message, Is.Not.Empty);
        Assert.That(registry.GetCommands(), Is.Empty);
    }

    [Test]
    public void Resolve_ExistingIdCollisionDoesNotRegisterOtherMethods()
    {
        var registry = new GatewayCommandRegistry();
        var resolver = new CommandResolver(registry);
        var original = resolver.Resolve(new BaseCommands()).Single();
        Assert.Throws<ArgumentException>(() => resolver.Resolve(new ConflictingCommands()));
        Assert.That(registry.GetCommands().Single(), Is.SameAs(original));
    }

    [Test]
    public void Resolve_PreservesHandlerExceptionsAndRejectsNullTask()
    {
        var commands = new CommandResolver(new()).Resolve(new FailingCommands());
        var args = JsonSerializer.SerializeToElement(new { });
        Assert.ThrowsAsync<InvalidOperationException>(async () => await commands.Single(c => c.Id == "throw").Handler(args, default));
        Assert.ThrowsAsync<InvalidOperationException>(async () => await commands.Single(c => c.Id == "nullTask").Handler(args, default));
    }

    [Test]
    public async Task ResolvedCommand_IsPublishedAndExecutedThroughTheRunningGateway()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = timeout.Token;
        await using var gateway = new GatewayBuilder("test", "Test", "secret").Build(new TcpJsonServer(IPAddress.Loopback, 0));
        gateway.Start();
        await using var client = await NodeDeckClientConnection.ConnectAsync("127.0.0.1", gateway.TcpPort!.Value, "secret", cancellationToken: ct);
        var owner = new ShortcutCommands(new CommandResolver(gateway.Commands));
        owner.Init();
        var catalog = (await client.GetCapabilitiesAsync(ct)).Deserialize<CapabilitiesDocument>(ProtocolJson.Options)!;
        ProtocolValidator.ValidateCapabilities(catalog);
        var command = catalog.Commands.Single();
        Assert.That(command.Id, Is.EqualTo("shortcut"));
        Assert.That(command.Title, Is.EqualTo("Open issues"));
        Assert.That(command.Parameters.Properties, Is.Empty, "The client should render a button that needs no argument prompt.");
        Assert.That(command.Availability.Enabled, Is.False);
        var rejected = (await client.InvokeAsync(command.Id, new { }, cancellationToken: ct)).Deserialize<CommandExecutionResult>(ProtocolJson.Options)!;
        Assert.That(rejected.Error!.Code, Is.EqualTo(ProtocolErrorCode.CommandUnavailable));
        Assert.That(owner.Calls, Is.Zero);
        owner.Ready = true;
        var refreshed = (await client.GetCapabilitiesAsync(ct)).Deserialize<CapabilitiesDocument>(ProtocolJson.Options)!;
        Assert.That(refreshed.Commands.Single().Availability.Enabled, Is.True);
        var result = (await client.InvokeAsync(command.Id, new { }, cancellationToken: ct)).Deserialize<CommandExecutionResult>(ProtocolJson.Options)!;
        Assert.That(result.Status, Is.EqualTo(ExecutionStatus.Completed));
        Assert.That(owner.Calls, Is.EqualTo(1));
    }

    private sealed class TypedCommands
    {
        [NodeCommand("sum", "Sum", Description = "Add two integers")]
        private int Sum(int first, int second = 2) => first + second;
        [NodeCommand("format", "Format")]
        public string[] Format(string prefix, bool enabled, double[] values) => [$"{prefix}:{enabled}:{values.Length}"];
        public void NotACommand() => throw new NotImplementedException();
    }

    private sealed class AsyncCommands
    {
        public bool Completed { get; private set; }
        public CancellationToken Token { get; private set; }
        [NodeCommand("sync", "Sync")]
        public void Sync(CancellationToken token) { Token = token; Completed = true; }
        [NodeCommand("task", "Task")]
        public async Task RunTask(CancellationToken token) { await Task.Yield(); Sync(token); }
        [NodeCommand("valueTask", "ValueTask")]
        public async ValueTask RunValueTask(CancellationToken token) { await Task.Yield(); Sync(token); }
        [NodeCommand("taskResult", "Task result")]
        public async Task<string> TaskResult(CancellationToken token) { await Task.Yield(); Sync(token); return "done"; }
        [NodeCommand("valueTaskResult", "ValueTask result")]
        public async ValueTask<string> ValueTaskResult(CancellationToken token) { await Task.Yield(); Sync(token); return "done"; }
    }

    private sealed class AvailabilityCommands
    {
        public bool Ready { get; set; }
        public int Reads { get; private set; }
        public bool CanConventional { get { Reads++; return Ready; } }
        public bool CanExplicit => false; // Explicit binding must override the convention.
        private bool IsReady() => Ready;
        public bool ReadyProperty => Ready;
        public Func<bool> Check { get; set; }
        public AvailabilityCommands() => Check = () => Ready;
        [NodeCommand("convention", "Convention", UnavailableReason = "Not ready")]
        public void Conventional() { }
        [NodeCommand("method", "Method", CanExecute = nameof(IsReady), UnavailableReason = "Not ready")]
        public void Explicit() { }
        [NodeCommand("property", "Property", CanExecute = nameof(ReadyProperty), UnavailableReason = "Not ready")]
        public void Property() { }
        [NodeCommand("delegate", "Delegate", CanExecute = nameof(Check), UnavailableReason = "Not ready")]
        public void Delegate() { }
    }

    public class BaseCommands
    {
        [NodeCommand("inherited", "Inherited")]
        public virtual string Run() => "base";
    }
    private sealed class DerivedCommands : BaseCommands { public override string Run() => "derived"; }
    public sealed class InvalidAvailability
    {
        public string CanRun => "yes";
        [NodeCommand("invalid", "Invalid")] public void Run() { }
    }
    public sealed class MissingAvailability
    {
        [NodeCommand("invalid", "Invalid", CanExecute = "Missing")] public void Run() { }
    }
    public sealed class UnsupportedSignature
    {
        [NodeCommand("valid", "Valid")] public void Valid() { }
        [NodeCommand("invalid", "Invalid")] public void Run(DateTime value) { }
    }
    public sealed class AsyncVoidCommand { [NodeCommand("invalid", "Invalid")] public async void Run() => await Task.Yield(); }
    public sealed class StaticCommand { [NodeCommand("invalid", "Invalid")] public static void Run() { } }
    public sealed class GenericCommand { [NodeCommand("invalid", "Invalid")] public void Run<T>() { } }
    public sealed class RefCommand { [NodeCommand("invalid", "Invalid")] public void Run(ref int value) { } }
    public sealed class NullDefaultCommand { [NodeCommand("invalid", "Invalid")] public void Run(string? value = null) { } }
    public sealed class DuplicateCommands
    {
        [NodeCommand("duplicate", "First")] public void First() { }
        [NodeCommand("duplicate", "Second")] public void Second() { }
    }
    private sealed class ConflictingCommands : BaseCommands { [NodeCommand("new", "New")] public void New() { } }
    private sealed class FailingCommands
    {
        [NodeCommand("throw", "Throw")] public void Throw() => throw new InvalidOperationException("Original failure");
        [NodeCommand("nullTask", "Null task")] public Task NullTask() => null!;
    }
    private sealed class ShortcutCommands(CommandResolver resolver)
    {
        public int Calls { get; private set; }
        public bool Ready { get; set; }
        public bool CanOpenIssues => Ready;
        public void Init() => resolver.Resolve(this);
        [NodeCommand("shortcut", "Open issues")]
        public void OpenIssues() => Calls++;
    }
}
