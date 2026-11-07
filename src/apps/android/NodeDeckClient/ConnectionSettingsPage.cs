using NodeDeckClient.Resources.Strings;
using NodeDeck.Core.Protocol;

namespace NodeDeckClient;

internal sealed class ConnectionSettingsPage : ContentPage
{
    private readonly ConnectionProfile _profile;
    private readonly Entry _gatewayId = new();
    private readonly Entry _serviceUuid = new();
    private readonly Entry _txUuid = new();
    private readonly Entry _rxUuid = new();
    private readonly Entry _host = new() { Placeholder = AppResources.GatewayAddress, Keyboard = Keyboard.Url };
    private readonly Entry _port = new() { Placeholder = AppResources.TcpPort, Keyboard = Keyboard.Numeric };
    private readonly Entry _secret = new() { Placeholder = AppResources.KeepCurrentSecret, IsPassword = true };

    public ConnectionSettingsPage(ConnectionProfile profile)
    {
        _profile = profile;
        Title = profile.DisplayName;
        var form = new VerticalStackLayout { Padding = 20, Spacing = 12 };
        if (profile.IsTcp)
        {
            _host.Text = profile.Host;
            _port.Text = profile.Port.ToString();
            form.Add(new Label { Text = AppResources.GatewayAddress });
            form.Add(_host);
            form.Add(new Label { Text = AppResources.TcpPort });
            form.Add(_port);
        }
        else
        {
            _serviceUuid.Text = profile.BleIdentifiers.ServiceUuid.ToString();
            _txUuid.Text = profile.BleIdentifiers.ClientToGatewayCharacteristicUuid.ToString();
            _rxUuid.Text = profile.BleIdentifiers.GatewayToClientCharacteristicUuid.ToString();
            form.Add(new Label { Text = AppResources.BleServiceUuid });
            form.Add(_serviceUuid);
            form.Add(new Label { Text = AppResources.BleClientToGatewayUuid });
            form.Add(_txUuid);
            form.Add(new Label { Text = AppResources.BleGatewayToClientUuid });
            form.Add(_rxUuid);
        }
        _gatewayId.Text = profile.ExpectedGatewayId;
        form.Add(new Label { Text = AppResources.ExpectedGatewayId });
        form.Add(_gatewayId);
        form.Add(new Label { Text = AppResources.NewGatewaySecret });
        form.Add(_secret);
        var save = new Button { Text = AppResources.Save };
        save.Clicked += OnSaveClicked;
        form.Add(save);
        Content = new ScrollView { Content = form };
    }

    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        var updated = _profile;
        if (_profile.IsTcp)
        {
            if (string.IsNullOrWhiteSpace(_host.Text) || !int.TryParse(_port.Text, out var port) || port is < 1 or > 65535)
            {
                await DisplayAlertAsync(AppResources.TcpSettings, AppResources.InvalidTcpSettings, AppResources.Ok);
                return;
            }
            updated = _profile with { Host = _host.Text.Trim(), Port = port };
        }
        else
        {
            if (!Guid.TryParse(_serviceUuid.Text, out var service) || !Guid.TryParse(_txUuid.Text, out var tx) ||
                !Guid.TryParse(_rxUuid.Text, out var rx) || service == Guid.Empty || tx == Guid.Empty || rx == Guid.Empty ||
                service == tx || service == rx || tx == rx)
            {
                await DisplayAlertAsync(AppResources.Connection, AppResources.InvalidBleIdentifiers, AppResources.Ok);
                return;
            }
            updated = updated with { BleIdentifiers = new BleServiceIdentifiers
            {
                ServiceUuid = service, ClientToGatewayCharacteristicUuid = tx, GatewayToClientCharacteristicUuid = rx
            } };
        }
        updated = updated with { ExpectedGatewayId = string.IsNullOrWhiteSpace(_gatewayId.Text) ? null : _gatewayId.Text.Trim() };
        IsEnabled = false;
        try
        {
            if (!string.IsNullOrWhiteSpace(_secret.Text))
                await SecureStorage.Default.SetAsync(_profile.SecretKey, _secret.Text);
            ConnectionProfile.Save(ConnectionProfile.Load().Select(p => p.Id == updated.Id ? updated : p).ToArray());
            await Navigation.PopAsync();
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync(AppResources.SaveSettingsFailed, ex.Message, AppResources.Ok);
        }
        finally
        {
            IsEnabled = true;
        }
    }
}
