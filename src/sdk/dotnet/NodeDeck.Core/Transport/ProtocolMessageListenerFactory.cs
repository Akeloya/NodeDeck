using System.Text.Json;
using System.Text.Json.Serialization;
using NLog;
using NodeDeck.Core.Settings;

namespace NodeDeck.Core.Transport;

/// <summary>Registers transports available to the host and reads their typed configuration.</summary>
public sealed class ProtocolMessageListenerFactory
{
    private sealed record Registration(Type SettingsType, Func<IListenerSettings, IProtocolMessageListener> Create);
    private readonly Dictionary<string, Registration> _transports = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        Converters = { new LogLevelConverter() }
    };

    public ProtocolMessageListenerFactory Register<T>(Func<T, IProtocolMessageListener> create)
        where T : IListenerSettings, new()
    {
        ArgumentNullException.ThrowIfNull(create);
        _transports.Add(new T().Type, new Registration(typeof(T), settings => create((T)settings)));
        return this;
    }

    public IProtocolMessageListener Create(IListenerSettings settings) => Resolve(settings).Create(settings);

    public IListenerSettings LoadSettings(string path)
    {
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { AllowDuplicateProperties = false });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(nameof(IListenerSettings.Type), out var type)
            || type.ValueKind != JsonValueKind.String)
            throw new JsonException("Listener settings must contain a string Type.");
        if (!_transports.TryGetValue(type.GetString()!, out var transport))
            throw new JsonException($"Unknown listener Type '{type.GetString()}'.");
        return (IListenerSettings)root.Deserialize(transport.SettingsType, Json)!;
    }

    public void SaveSettings(string path, IListenerSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, Resolve(settings).SettingsType, Json);
        var temporaryPath = Path.GetFullPath(path) + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private Registration Resolve(IListenerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!_transports.TryGetValue(settings.Type, out var entry) || entry.SettingsType != settings.GetType())
            throw new ArgumentException($"Unregistered listener settings '{settings.GetType().Name}'.", nameof(settings));
        return entry;
    }

    private sealed class LogLevelConverter : JsonConverter<LogLevel>
    {
        public override bool HandleNull => true;

        public override LogLevel Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                var name = reader.GetString();
                if (string.Equals(name, LogLevel.Off.Name, StringComparison.OrdinalIgnoreCase))
                    return LogLevel.Off;
                var level = LogLevel.AllLevels.FirstOrDefault(level =>
                    string.Equals(level.Name, name, StringComparison.OrdinalIgnoreCase));
                if (level is not null)
                    return level;
            }
            throw new JsonException("Unknown listener LogLevel.");
        }

        public override void Write(Utf8JsonWriter writer, LogLevel value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Name);
    }
}
