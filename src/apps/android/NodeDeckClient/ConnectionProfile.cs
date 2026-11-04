using System.Text.Json;
using System.Text.Json.Serialization;

namespace NodeDeckClient;

public sealed record ConnectionProfile(string Id, string Name, string? Host, int Port = 5050)
{
    private const string ProfilesKey = "connections.profiles";

    [JsonIgnore] public bool IsTcp => Host is not null;
    [JsonIgnore] public string Icon => IsTcp ? "wifi.png" : "bluetooth.png";
    [JsonIgnore] public string Detail => IsTcp ? $"{Host}:{Port}" : "Bluetooth LE";
    [JsonIgnore] public string SecretKey => $"connection.{Id}.secret";

    public static ConnectionProfile[] Load()
    {
        if (Preferences.Default.Get<string?>(ProfilesKey, null) is { } json)
            return JsonSerializer.Deserialize<ConnectionProfile[]>(json) ?? [];

        ConnectionProfile[] profiles =
        [
            new("tcp", "Wi-Fi · разработка", "10.0.2.2"),
            new("ble", "Bluetooth · разработка", null)
        ];
        Save(profiles);
        return profiles;
    }

    public static void Save(ConnectionProfile[] profiles) =>
        Preferences.Default.Set(ProfilesKey, JsonSerializer.Serialize(profiles));
}
