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
            var listeners = new ProtocolMessageListenerFactory()
                .Register<TcpListenerSettings>(settings => new TcpJsonServer(settings))
                .Register<BluetoothListenerSettings>(settings => new WindowsBleProtocolListener(settings));
            var factory = new GatewayFactory(listeners);
            try
            {
                GatewaySettings settings;
                if (options.GatewaySettings is not null)
                {
                    settings = factory.LoadSettings(ResolveSettingsPath(options.GatewaySettings));
                }
                else
                {
                    var mode = options.Mode switch
                    {
                        "executor" => GatewayMode.Executor,
                        "node" => GatewayMode.Node,
                        _ => throw new ArgumentException("Mode must be 'executor' or 'node'.")
                    };
                    IListenerSettings[] transports = options.Settings.Any()
                        ? options.Settings.Select(path => listeners.LoadSettings(ResolveSettingsPath(path))).ToArray()
                        : [ReadListenerSettings(options)];
                    if (options.SaveSettings is not null)
                    {
                        if (transports.Length != 1)
                            throw new ArgumentException("--save-settings requires a single listener configuration.");
                        listeners.SaveSettings(options.SaveSettings, transports[0]);
                        return 0;
                    }
                    settings = new GatewaySettings
                    {
                        Mode = mode, GatewayId = options.GatewayId, Name = options.GatewayName,
                        Description = options.GatewayDescription, SharedSecret = options.Secret,
                        Listeners = transports.Select(listeners.SerializeSettings).ToArray()
                    };
                }

                await using IGateway gateway = await factory.CreateAsync(settings, settings.Mode == GatewayMode.Executor ? commands =>
                {
                    DemoCommands.Register(commands);
                    new ProjectCommands(new CommandResolver(commands)).Init();
                } : null);
                using var shutdown = new CancellationTokenSource();
                ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; shutdown.Cancel(); };
                System.Console.CancelKeyPress += cancel;
                try
                {
                    gateway.Start();
                    await gateway.Ready.WaitAsync(shutdown.Token);
                    System.Console.WriteLine($"{gateway.Info.Kind}: {gateway.Info.Name} ({gateway.Info.Id}). {gateway.Info.Description}");
                    System.Console.WriteLine("Press Ctrl+C to stop.");
                    await gateway.RunAsync(shutdown.Token);
                }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
                finally { System.Console.CancelKeyPress -= cancel; }
                return 0;
            }
            catch (Exception ex)
            {
                System.Console.Error.WriteLine(ex.Message);
                return 2;
            }
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
