namespace NodeDeckClient;

public partial class MainPage : ContentPage
{
    private ConnectionProfile[] _profiles = [];
    private string _query = "";
    private ImageButton? _transportFilter;
    private SessionPage? _session;
    private bool _opening;

    public MainPage()
    {
        InitializeComponent();
    }

    private void OnSearchChanged(object? sender, TextChangedEventArgs e)
    {
        _query = e.NewTextValue?.Trim() ?? "";
        FilterConnections();
    }

    private void OnTransportFilterClicked(object? sender, EventArgs e)
    {
        if (sender is not ImageButton button)
            return;

        _transportFilter = _transportFilter == button ? null : button;
        WifiFilter.BorderWidth = _transportFilter == WifiFilter ? 2 : 0;
        BluetoothFilter.BorderWidth = _transportFilter == BluetoothFilter ? 2 : 0;
        FilterConnections();
    }

    private void FilterConnections()
    {
        var selected = (Connections.SelectedItem as ConnectionProfile)?.Id;
        var matches = _profiles
            .Where(p => _transportFilter is null || _transportFilter == (p.IsTcp ? WifiFilter : BluetoothFilter))
            .Where(p => p.Name.Contains(_query, StringComparison.OrdinalIgnoreCase)
                || p.Detail.Contains(_query, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Connections.ItemsSource = matches;
        Connections.SelectedItem = matches.FirstOrDefault(p => p.Id == selected) ?? matches.FirstOrDefault();
        UpdateButtons();
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void UpdateButtons() =>
        ConnectButton.IsEnabled = EditButton.IsEnabled = !_opening && Connections.SelectedItem is ConnectionProfile;

    private async void OnConnectClicked(object? sender, EventArgs e)
    {
        if (_opening || Connections.SelectedItem is not ConnectionProfile profile)
            return;
        _opening = true;
        UpdateButtons();
        try
        {
            if (_session is not null)
                await _session.Completion;
            _session = new SessionPage(profile);
            await Navigation.PushAsync(_session);
        }
        finally
        {
            _opening = false;
            UpdateButtons();
        }
    }

    private async void OnEditClicked(object? sender, EventArgs e)
    {
        if (Connections.SelectedItem is not ConnectionProfile profile)
            return;

        await Navigation.PushAsync(new ConnectionSettingsPage(profile));
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _profiles = ConnectionProfile.Load();
        FilterConnections();
    }
}
