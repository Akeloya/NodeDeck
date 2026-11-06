using System.Text.Json;
using NodeDeck.Core.Protocol;
using NodeDeck.Gateway;
using WindowsInput;
using WindowsInput.Native;
using ProtocolValueType = NodeDeck.Core.Protocol.ValueType;

namespace NodeDeck.Console;

internal static class DemoCommands
{
    public static void Register(GatewayBuilder builder)
    {
        var parameters = new ValueDescriptor
        {
            Type = ProtocolValueType.Object,
            Properties = new Dictionary<string, ValueDescriptor>
            {
                ["message"] = new() { Type = ProtocolValueType.String, MinLength = 1, MaxLength = 200 }
            },
            Required = ["message"],
            AdditionalProperties = false
        };
        var resultDescriptor = new ValueDescriptor
        {
            Type = ProtocolValueType.Object,
            Properties = new Dictionary<string, ValueDescriptor>
            {
                ["reply"] = new() { Type = ProtocolValueType.String }
            },
            Required = ["reply"],
            AdditionalProperties = false
        };

        foreach (var (id, title) in new[]
        {
            ("phone.hello", "Say hello"),
            ("phone.status", "Get status"),
            ("phone.action", "Run action")
        })
        {
            builder.AddCommand(new GatewayCommand(id, title, "Demo command invoked from the phone",
                parameters, resultDescriptor,
                (arguments, _) =>
                {
                    var message = arguments.GetProperty("message").GetString()!;
                    System.Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] Phone command: {id}; message: {message}");
                    return ValueTask.FromResult(JsonSerializer.SerializeToElement(new { reply = $"Console received '{message}' via {id}." }));
                }));
        }

        builder.AddCommand(new GatewayCommand("key.action", "Open explorer", "Command to generate keyboard event",
            parameters, resultDescriptor,
            (arguments, _) =>
            {
                new InputSimulator().Keyboard.ModifiedKeyStroke(VirtualKeyCode.LWIN, VirtualKeyCode.VK_E);
                return ValueTask.FromResult(JsonSerializer.SerializeToElement(new { reply = "Command Win+E activated" }));
            }));
    }
}
