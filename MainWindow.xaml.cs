using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;

namespace VPN;

public partial class MainWindow : Window
{
    private readonly VpnService _vpn = new();
    private readonly ObservableCollection<VpnProfile> _profiles = new();
    private readonly string _store = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VPN", "profiles.json");
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        LoadProfiles();
        ProfileBox.ItemsSource = _profiles;
        if (_profiles.Count > 0) ProfileBox.SelectedIndex = 0;
        RefreshSelection();
    }

    private void ModeChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => RefreshSelection();
    private void ProfileChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => RefreshSelection();

    private void RefreshSelection()
    {
        if (!IsLoaded) return;
        var p = ProfileBox.SelectedItem as VpnProfile;
        ServerText.Text = p is null ? "No server selected" : p.Server;
        LatencyText.Text = p is null ? "Add a profile to begin" : p.LastLatencyMs > 0 ? $"Last ping: {p.LastLatencyMs} ms" : "Latency not measured";
        ConnectButton.Content = _vpn.IsConnected ? "DISCONNECT" : "CONNECT";
        StatusText.Text = _vpn.IsConnected ? $"● CONNECTED • {_vpn.ActiveProfile?.Name}" : "● DISCONNECTED";
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            if (_vpn.IsConnected) { await _vpn.DisconnectAsync(); return; }
            var profile = await ResolveProfileAsync();
            if (profile is null) { MessageBox.Show("Add at least one VPN profile first.", "VPN"); return; }
            await _vpn.ConnectAsync(profile);
            RefreshSelection();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "VPN connection failed", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { _busy = false; RefreshSelection(); }
    }

    private async Task<VpnProfile?> ResolveProfileAsync()
    {
        if (_profiles.Count == 0) return null;
        var mode = ModeBox.SelectedIndex switch { 1 => VpnProtocol.IKEv2, 2 => VpnProtocol.OpenVPN, _ => VpnProtocol.Auto };
        var candidates = mode == VpnProtocol.Auto ? _profiles.ToList() : _profiles.Where(p => p.Protocol == mode).ToList();
        if (candidates.Count == 0) return null;
        if (mode == VpnProtocol.Auto)
        {
            foreach (var p in candidates) p.LastLatencyMs = await VpnService.PingAsync(p.Server);
            candidates = candidates.OrderBy(p => p.LastLatencyMs < 0 ? int.MaxValue : p.LastLatencyMs).ToList();
        }
        var selected = candidates.FirstOrDefault();
        if (selected is not null) ProfileBox.SelectedItem = selected;
        return selected;
    }

    private void AddProfile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ProfileDialog { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var profile = dialog.Profile!;
        _profiles.Add(profile);
        ProfileBox.SelectedItem = profile;
        SaveProfiles();
    }

    private void RemoveProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileBox.SelectedItem is not VpnProfile p) return;
        _profiles.Remove(p); SaveProfiles(); RefreshSelection();
    }

    private void LoadProfiles()
    {
        try
        {
            if (File.Exists(_store))
                foreach (var p in JsonSerializer.Deserialize<List<VpnProfile>>(File.ReadAllText(_store)) ?? new()) _profiles.Add(p);
        }
        catch { }
    }

    private void SaveProfiles()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_store)!);
            File.WriteAllText(_store, JsonSerializer.Serialize(_profiles, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

public sealed class ProfileDialog : Window
{
    public VpnProfile? Profile { get; private set; }
    private readonly System.Windows.Controls.TextBox _name = new();
    private readonly System.Windows.Controls.TextBox _server = new();
    private readonly System.Windows.Controls.ComboBox _protocol = new();
    private readonly System.Windows.Controls.TextBox _config = new();

    public ProfileDialog()
    {
        Title = "Add VPN profile"; Width = 500; Height = 390; WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = System.Windows.Media.Brushes.White;
        _protocol.ItemsSource = Enum.GetValues<VpnProtocol>().Where(x => x != VpnProtocol.Auto); _protocol.SelectedIndex = 0;
        var grid = new System.Windows.Controls.Grid { Margin = new Thickness(24) };
        for (int i = 0; i < 6; i++) grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = i == 5 ? new GridLength(55) : GridLength.Auto });
        Add(grid, "Profile name", _name, 0); Add(grid, "Server / hostname", _server, 1); Add(grid, "Protocol", _protocol, 2); Add(grid, "OpenVPN .ovpn path (OpenVPN only)", _config, 3);
        var browse = new System.Windows.Controls.Button { Content = "Browse…" }; browse.Click += (_, _) => { var d = new OpenFileDialog { Filter = "OpenVPN config (*.ovpn)|*.ovpn|All files (*.*)|*.*" }; if (d.ShowDialog() == true) _config.Text = d.FileName; }; System.Windows.Controls.Grid.SetRow(browse, 4); grid.Children.Add(browse);
        var save = new System.Windows.Controls.Button { Content = "Add profile", HorizontalAlignment = HorizontalAlignment.Right, Width = 130 }; save.Click += Save_Click; System.Windows.Controls.Grid.SetRow(save, 5); grid.Children.Add(save);
        Content = grid;
    }

    private static void Add(System.Windows.Controls.Grid g, string label, System.Windows.Controls.Control c, int row)
    {
        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(0, 0, 0, 8) }; panel.Children.Add(new System.Windows.Controls.TextBlock { Text = label, Foreground = System.Windows.Media.Brushes.DimGray }); panel.Children.Add(c); System.Windows.Controls.Grid.SetRow(panel, row); g.Children.Add(panel);
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_name.Text) || string.IsNullOrWhiteSpace(_server.Text)) { MessageBox.Show("Name and server are required."); return; }
        var protocol = (VpnProtocol)_protocol.SelectedItem;
        if (protocol == VpnProtocol.OpenVPN && string.IsNullOrWhiteSpace(_config.Text)) { MessageBox.Show("Select an .ovpn file for OpenVPN."); return; }
        Profile = new VpnProfile { Name = _name.Text.Trim(), Server = _server.Text.Trim(), Protocol = protocol, ConfigPath = _config.Text.Trim() }; DialogResult = true; Close();
    }
}
