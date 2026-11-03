using System.Diagnostics;
using System.Text.Json;
using NodeDeck.Gateway;
using NodeDeck.Core.Protocol;
using NodeDeck.Core.Settings;
using NodeDeck.Core.Transport;
using NodeDeck.Transport.Ble.Windows;
using NodeDeck.Transport.Tcp;
using ProtocolValueType = NodeDeck.Core.Protocol.ValueType;

namespace NodeDeck.Console
{
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            var factory = new ProtocolMessageListenerFactory()
                .Register<TcpListenerSettings>(settings => new TcpJsonServer(settings))
                .Register<BluetoothListenerSettings>(settings => new WindowsBleProtocolListener(settings));

            string secret;
            IProtocolMessageListener listener;
            try
            {
                secret = ReadOption(args, "--secret") ?? "nodedeck-dev";
                var output = ReadOption(args, "--save-settings");
                var settings = ReadOption(args, "--settings") is { } path
                    ? factory.LoadSettings(path)
                    : ReadListenerSettings(args);
                listener = factory.Create(settings);
                if (output is not null)
                {
                    await using (listener)
                        factory.SaveSettings(output, settings);
                    return 0;
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or ArgumentException or UnauthorizedAccessException)
            {
                System.Console.Error.WriteLine(ex.Message);
                return 2;
            }

            if (secret == "nodedeck-dev")
                System.Console.WriteLine("Using development secret 'nodedeck-dev'. Set --secret to change it.");

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

            var gateway = builder.Build(listener);
            await using (gateway.ConfigureAwait(false))
            {
                gateway.ClientLoggedOut += () => System.Console.WriteLine("Client logged out. Session ended.");
                using var shutdown = new CancellationTokenSource();
                System.Console.CancelKeyPress += (_, e) =>
                {
                    e.Cancel = true;
                    shutdown.Cancel();
                };

                try
                {
                    gateway.Start();
                    await listener.Ready.WaitAsync(shutdown.Token);
                }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
                {
                    return 0;
                }
                catch (Exception ex)
                {
                    System.Console.Error.WriteLine($"Listener startup failed: {ex.GetBaseException().Message}");
                    return 1;
                }
                System.Console.WriteLine("Press Ctrl+C to stop.");

                try
                {
                    await gateway.RunAsync(shutdown.Token);
                }
                catch (OperationCanceledException) { /* Ctrl+C requests a graceful shutdown. */ }
            }

            return 0;
        }

        private static IListenerSettings ReadListenerSettings(string[] args)
        {
            var debugProfile = Debugger.IsAttached ? ReadOption(args, "--debug-profile") : null;
            var transport = ReadOption(args, "--transport") ??
                (debugProfile is "tcp" or "bluetooth" ? debugProfile : SelectTransport());
            if (transport == "bluetooth")
                return new BluetoothListenerSettings();
            if (transport != "tcp")
                throw new ArgumentException("Transport must be 'tcp' or 'bluetooth'.");
            var settings = new TcpListenerSettings();
            settings.IPAddress = ReadOption(args, "--bind") ?? settings.IPAddress;
            if (ReadOption(args, "--port") is { } portText)
            {
                if (!int.TryParse(portText, out var port))
                    throw new ArgumentException($"Invalid port '{portText}'.");
                settings.Port = port;
            }
            return settings;
        }

        private static string? ReadOption(string[] arguments, string key)
        {
            var index = Array.IndexOf(arguments, key);
            if (index < 0)
                return null;
            if (index + 1 == arguments.Length || arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Missing value for {key}.");
            return arguments[index + 1];
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
