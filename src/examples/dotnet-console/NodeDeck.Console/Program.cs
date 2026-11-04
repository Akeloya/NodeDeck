using System.Diagnostics;
using System.Text.Json;
using CommandLine;
using NodeDeck.Core.Settings;
using NodeDeck.Core.Transport;
using NodeDeck.Gateway;
using NodeDeck.Transport.Ble.Windows;
using NodeDeck.Transport.Tcp;

namespace NodeDeck.Console
{
    internal static class Program
    {
        private static Task<int> Main(string[] args) =>
            Parser.Default.ParseArguments<ConsoleOptions>(args).MapResult(
                RunAsync,
                errors => Task.FromResult(errors.All(error => error is HelpRequestedError or VersionRequestedError) ? 0 : 2));

        private static async Task<int> RunAsync(ConsoleOptions options)
        {
            var factory = new ProtocolMessageListenerFactory()
                .Register<TcpListenerSettings>(settings => new TcpJsonServer(settings))
                .Register<BluetoothListenerSettings>(settings => new WindowsBleProtocolListener(settings));

            var listeners = new List<IProtocolMessageListener>();
            GatewayServer? gateway = null;
            try
            {
                IListenerSettings[] settings = options.Settings.Any()
                    ? options.Settings.Select(path => factory.LoadSettings(ResolveSettingsPath(path))).ToArray()
                    : [ReadListenerSettings(options)];
                if (options.SaveSettings is not null && settings.Length != 1)
                    throw new ArgumentException("--save-settings requires a single listener configuration.");

                foreach (var configuration in settings)
                    listeners.Add(factory.Create(configuration));

                if (options.SaveSettings is not null)
                {
                    factory.SaveSettings(options.SaveSettings, settings[0]);
                    return 0;
                }

                var builder = new GatewayBuilder("console-demo", "NodeDeck Console Demo", options.Secret);
                DemoCommands.Register(builder);
                gateway = builder.BuildWithListeners(listeners);
            }
            catch (Exception ex) when (ex is JsonException or IOException or ArgumentException or UnauthorizedAccessException)
            {
                System.Console.Error.WriteLine(ex.Message);
                return 2;
            }
            finally
            {
                if (gateway is null)
                    foreach (var listener in listeners)
                        await listener.DisposeAsync();
            }

            if (options.Secret == "nodedeck-dev")
                System.Console.WriteLine("Using development secret 'nodedeck-dev'. Set --secret to change it.");

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
                    await Task.WhenAll(listeners.Select(listener => listener.Ready)).WaitAsync(shutdown.Token);
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

        private static string ResolveSettingsPath(string path)
        {
            if (Path.IsPathRooted(path) || File.Exists(path))
                return path;

            var bundledPath = Path.Combine(AppContext.BaseDirectory, path);
            return File.Exists(bundledPath) ? bundledPath : path;
        }

        private static IListenerSettings ReadListenerSettings(ConsoleOptions options)
        {
            var debugProfile = Debugger.IsAttached ? options.DebugProfile : null;
            var transport = options.Transport ??
                (debugProfile is "tcp" or "bluetooth" ? debugProfile : SelectTransport());
            if (transport == "bluetooth")
                return new BluetoothListenerSettings();
            if (transport != "tcp")
                throw new ArgumentException("Transport must be 'tcp' or 'bluetooth'.");
            var settings = new TcpListenerSettings();
            settings.IPAddress = options.Bind ?? settings.IPAddress;
            settings.Port = options.Port ?? settings.Port;
            return settings;
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
