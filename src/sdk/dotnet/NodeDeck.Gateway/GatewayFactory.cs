using System.Text.Json;
using System.Text.Json.Serialization;
using NodeDeck.Core.Transport;

namespace NodeDeck.Gateway;

public sealed class GatewayFactory(ProtocolMessageListenerFactory listenerFactory)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public GatewaySettings LoadSettings(string path) =>
        JsonSerializer.Deserialize<GatewaySettings>(File.ReadAllText(path), Json)
        ?? throw new JsonException("Gateway settings are required.");

    public async Task<IGateway> CreateAsync(GatewaySettings settings, Action<GatewayCommandRegistry>? configureCommands = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        if (settings.Mode == GatewayMode.Node && configureCommands is not null)
            throw new ArgumentException("A routing node cannot register local executable commands.", nameof(configureCommands));
        var listeners = new List<IProtocolMessageListener>();
        try
        {
            foreach (var listener in settings.Listeners)
                listeners.Add(listenerFactory.Create(listenerFactory.ReadSettings(listener)));
            if (settings.Mode == GatewayMode.Node)
                return new GatewayNode(settings, listeners);
            var server = new GatewayServer(settings.GatewayId, settings.Name, settings.Description, settings.SharedSecret,
                [], listeners, settings.Sessions);
            configureCommands?.Invoke(server.Commands);
            return server;
        }
        catch
        {
            foreach (var listener in listeners)
                await listener.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
