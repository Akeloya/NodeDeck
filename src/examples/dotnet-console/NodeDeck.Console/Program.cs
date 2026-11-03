using System.Diagnostics;
using System.Net;
using System.Text.Json;
using NodeDeck.Gateway;
using NodeDeck.Protocol;
using NodeDeck.Transport.Ble.Windows;
using ProtocolValueType = NodeDeck.Protocol.ValueType;

namespace NodeDeck.Console
{
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            var bindText = ReadOption(args, "--bind") ?? "0.0.0.0";
            var portText = ReadOption(args, "--port") ?? "5050";
            var secret = ReadOption(args, "--secret") ?? "nodedeck-dev";
            var transport = ReadOption(args, "--transport");
            if (transport is null)
            {
                var debugProfile = Debugger.IsAttached ? ReadOption(args, "--debug-profile") : null;
                transport = debugProfile is "tcp" or "bluetooth" ? debugProfile : SelectTransport();
            }
            if (transport is not ("tcp" or "bluetooth"))
            {
                System.Console.Error.WriteLine("Transport must be 'tcp' or 'bluetooth'.");
                return 2;
            }
            if (!IPAddress.TryParse(bindText, out var bindAddress))
            {
                System.Console.Error.WriteLine($"Invalid bind IP: {bindText}");
                return 2;
            }
            if (!int.TryParse(portText, out var port) || port is < 0 or > 65535)
            {
                System.Console.Error.WriteLine($"Invalid port: {portText}");
                return 2;
            }
            if (secret == "nodedeck-dev") System.Console.WriteLine("Using development secret 'nodedeck-dev'. Set --secret to change it.");

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

            var builder = new GatewayBuilder("console-demo", "NodeDeck Console Demo", secret);
            foreach (var (commandId, title) in new[]
            {
                ("phone.hello", "Say hello"),
                ("phone.status", "Get status"),
                ("phone.action", "Run action")
            })
            {
                var id = commandId;
                builder.AddCommand(
                    new CommandDefinition(id, title, "Demo command invoked from the phone", parameters, resultDescriptor, new CommandAvailability(true)),
                    (arguments, _) =>
                    {
                        var message = arguments.GetProperty("message").GetString()!;
                        System.Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] Phone command: {id}; message: {message}");
                        return ValueTask.FromResult(JsonSerializer.SerializeToElement(new { reply = $"Console received '{message}' via {id}." }));
                    });
            }

            var bleListener = transport == "bluetooth" ? new WindowsBleProtocolListener() : null;
            if (bleListener is not null)
                bleListener.Diagnostic += message => System.Console.WriteLine($"[BLE] {message}");
            var gateway = bleListener is null ? builder.Build(bindAddress, port) : builder.Build(bleListener);
            await using (gateway.ConfigureAwait(false))
            {
                gateway.ClientLoggedOut += () => System.Console.WriteLine("Client logged out. Session ended.");
                gateway.Start();
                if (transport == "tcp")
                    System.Console.WriteLine($"Gateway '{gateway.GatewayId}' listening on TCP {bindAddress}:{gateway.TcpPort}");
                else
                {
                    System.Console.WriteLine($"Scanning for the phone's NodeDeck BLE service ({WindowsBleProtocolListener.ServiceUuid}).");
                    System.Console.WriteLine("On the phone, choose Bluetooth LE and tap Connect; keep this window open.");
                }

                using var shutdown = new CancellationTokenSource();
                System.Console.CancelKeyPress += (_, e) =>
                {
                    e.Cancel = true;
                    shutdown.Cancel();
                };

                if (bleListener is not null)
                {
                    try { await bleListener.Ready.WaitAsync(shutdown.Token); }
                    catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { return 0; }
                    catch (Exception ex)
                    {
                        System.Console.Error.WriteLine($"Bluetooth LE connection failed: {ex.GetBaseException().Message}");
                        return 1;
                    }
                    System.Console.WriteLine("Phone connected over Bluetooth LE.");
                }
                System.Console.WriteLine("Press Ctrl+C to stop.");

                try { await gateway.RunAsync(shutdown.Token); }
                catch (OperationCanceledException) { /* Ctrl+C requests a graceful shutdown. */ }
            }

            return 0;
        }

        private static string? ReadOption(string[] arguments, string key)
        {
            var index = Array.IndexOf(arguments, key);
            return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
        }

        private static string SelectTransport()
        {
            System.Console.WriteLine("Select transport:");
            System.Console.WriteLine("  1. TCP");
            System.Console.WriteLine("  2. Bluetooth LE");
            System.Console.Write("Choice [1/2]: ");
            return System.Console.ReadLine()?.Trim() switch { "2" => "bluetooth", _ => "tcp" };
        }

    }
}
