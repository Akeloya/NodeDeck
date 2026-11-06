using System.Globalization;
using System.Resources;

namespace NodeDeckClient.Resources.Strings;

/// <summary>UI strings resolved using the system UI culture, with English as the fallback.</summary>
public static class AppResources
{
    public static ResourceManager ResourceManager { get; } =
        new("NodeDeckClient.Resources.Strings.AppResources", typeof(AppResources).Assembly);

    private static string GetString(string name) =>
        ResourceManager.GetString(name, CultureInfo.CurrentUICulture)
        ?? throw new MissingManifestResourceException($"Missing UI resource: {name}");

    public static string Connections => GetString(nameof(Connections));
    public static string ScanQrCode => GetString(nameof(ScanQrCode));
    public static string SavedConnections => GetString(nameof(SavedConnections));
    public static string WifiFilter => GetString(nameof(WifiFilter));
    public static string BluetoothFilter => GetString(nameof(BluetoothFilter));
    public static string FindConnection => GetString(nameof(FindConnection));
    public static string Connect => GetString(nameof(Connect));
    public static string Configure => GetString(nameof(Configure));
    public static string NoConnectionsFound => GetString(nameof(NoConnectionsFound));
    public static string Connection => GetString(nameof(Connection));
    public static string Retry => GetString(nameof(Retry));
    public static string OtherConnections => GetString(nameof(OtherConnections));
    public static string CommandMessage => GetString(nameof(CommandMessage));
    public static string GatewayAddress => GetString(nameof(GatewayAddress));
    public static string TcpPort => GetString(nameof(TcpPort));
    public static string KeepCurrentSecret => GetString(nameof(KeepCurrentSecret));
    public static string NewGatewaySecret => GetString(nameof(NewGatewaySecret));
    public static string Save => GetString(nameof(Save));
    public static string TcpSettings => GetString(nameof(TcpSettings));
    public static string InvalidTcpSettings => GetString(nameof(InvalidTcpSettings));
    public static string SaveSettingsFailed => GetString(nameof(SaveSettingsFailed));
    public static string ConnectingToGateway => GetString(nameof(ConnectingToGateway));
    public static string WaitingForBluetoothGateway => GetString(nameof(WaitingForBluetoothGateway));
    public static string MissingCommandList => GetString(nameof(MissingCommandList));
    public static string ConnectionRestored => GetString(nameof(ConnectionRestored));
    public static string GatewayDisconnected => GetString(nameof(GatewayDisconnected));
    public static string GatewayTimeout => GetString(nameof(GatewayTimeout));
    public static string ReconnectionFailed => GetString(nameof(ReconnectionFailed));
    public static string ConnectionFailed => GetString(nameof(ConnectionFailed));
    public static string ConnectionLost => GetString(nameof(ConnectionLost));
    public static string ReconnectingToProfile => GetString(nameof(ReconnectingToProfile));
    public static string BluetoothAndroidOnly => GetString(nameof(BluetoothAndroidOnly));
    public static string Commands => GetString(nameof(Commands));
    public static string NoCommands => GetString(nameof(NoCommands));
    public static string JsonArguments => GetString(nameof(JsonArguments));
    public static string Run => GetString(nameof(Run));
    public static string Cancel => GetString(nameof(Cancel));
    public static string InvalidJson => GetString(nameof(InvalidJson));
    public static string WifiDevelopmentProfile => GetString(nameof(WifiDevelopmentProfile));
    public static string BluetoothDevelopmentProfile => GetString(nameof(BluetoothDevelopmentProfile));
    public static string Ok => GetString(nameof(Ok));
    public static string DefaultMessage => GetString(nameof(DefaultMessage));
    public static string Home => GetString(nameof(Home));
    public static string ClosePanel => GetString(nameof(ClosePanel));
}

