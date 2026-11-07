using System.Globalization;
using NodeDeck.Core.Protocol;
using System.Text.Json;
using System.Text.Json.Serialization;
using NodeDeckClient.Resources.Strings;

namespace NodeDeckClient;

public sealed record ConnectionProfile(string Id, string Name, string? Host, int Port = 5050)
{
    public BleServiceIdentifiers BleIdentifiers { get; init; } = new();
    public string? ExpectedGatewayId { get; init; }

    // One installation identity shared by all BLE/TCP profiles; never a hardware MAC address.
    public static string DeviceId
    {
        get
        {
            const string key = "device.installationId";
            var id = Preferences.Default.Get<string?>(key, null);
            if (id is null)
            {
                id = Guid.NewGuid().ToString("N");
                Preferences.Default.Set(key, id);
            }
            return id;
        }
    }

    private const string ProfilesKey = "connections.profiles";

    [JsonIgnore] public bool IsTcp => Host is not null;
    [JsonIgnore] public string Icon => IsTcp ? "wifi.png" : "bluetooth.png";
    [JsonIgnore] public string Detail => IsTcp ? $"{Host}:{Port}" : "Bluetooth LE";
    [JsonIgnore] public string SecretKey => $"connection.{Id}.secret";

    // Resolve built-in names at display time, including names saved by older versions.
    // Custom names are user data and must not change with the system language.
    [JsonIgnore]
    public string DisplayName
    {
        get
        {
            var resourceKey = Id switch
            {
                "tcp" => nameof(AppResources.WifiDevelopmentProfile),
                "ble" => nameof(AppResources.BluetoothDevelopmentProfile),
                _ => null
            };
            if (resourceKey is null)
                return Name;

            var resources = AppResources.ResourceManager;
            var neutralName = resources.GetString(resourceKey, CultureInfo.InvariantCulture);
            var russianName = resources.GetString(resourceKey, CultureInfo.GetCultureInfo("ru"));
            return Name == neutralName || Name == russianName
                ? resources.GetString(resourceKey, CultureInfo.CurrentUICulture)!
                : Name;
        }
    }

    public static ConnectionProfile[] Load()
    {
        if (Preferences.Default.Get<string?>(ProfilesKey, null) is { } json)
            return JsonSerializer.Deserialize<ConnectionProfile[]>(json) ?? [];

        ConnectionProfile[] profiles =
        [
            new("tcp", AppResources.ResourceManager.GetString(nameof(AppResources.WifiDevelopmentProfile), CultureInfo.InvariantCulture)!, "10.0.2.2"),
            new("ble", AppResources.ResourceManager.GetString(nameof(AppResources.BluetoothDevelopmentProfile), CultureInfo.InvariantCulture)!, null)
        ];
        Save(profiles);
        return profiles;
    }

    public static void Save(ConnectionProfile[] profiles) =>
        Preferences.Default.Set(ProfilesKey, JsonSerializer.Serialize(profiles));
}
