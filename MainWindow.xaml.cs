using Microsoft.Win32;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VPN;

public partial class MainWindow : Window
{
    private readonly VpnService _vpn = new();
    private readonly KillSwitchService _killSwitch = new();
    private readonly ObservableCollection<VpnProfile> _profiles = new();
    private readonly ObservableCollection<ServerLocation> _locations = new(ServerLocation.All);
    private readonly string _store = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VPN", "profiles.json");
    private bool _busy;
    private bool _initializing = true;
    private bool _manualDisconnect;
    private bool _reconnecting;

    public MainWindow()
    {
        InitializeComponent();
        LoadProfiles();
        ProfileBox.ItemsSource = _profiles;
        LocationList.ItemsSource = _locations;
        if (_locations.Count > 0) LocationList.SelectedIndex = 2;
        if (_profiles.Count > 0) ProfileBox.SelectedIndex = 0;
        _vpn.StateChanged += Vpn_StateChanged;
        Loaded += (_, _) => RefreshSelection();
        KillSwitchBox.IsChecked = _killSwitch.IsActive;
        _initializing = false;
        RefreshSelection();
    }

    private void ModeChanged(object sender, SelectionChangedEventArgs e) => RefreshSelection();
    private void ProfileChanged(object sender, SelectionChangedEventArgs e) => RefreshSelection();

    private void LocationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || !IsLoaded) return;
        if (LocationList.SelectedItem is ServerLocation location)
        {
            var matching = _profiles.FirstOrDefault(p => string.Equals(p.Region, location.Region, StringComparison.OrdinalIgnoreCase));
            if (matching is not null) ProfileBox.SelectedItem = matching;
            else
            {
                ServerText.Text = location.DisplayName;
                LatencyText.Text = "Reference location only — add a real provider hostname/configuration.";
            }
        }
        RefreshSelection(preserveReferenceLocation: true);
    }

    private void RefreshSelection(bool preserveReferenceLocation = false)
    {
        if (!IsLoaded) return;
        var selected = _vpn.IsConnected ? _vpn.ActiveProfile : ProfileBox.SelectedItem as VpnProfile;
        if (selected is not null)
        {
            ServerText.Text = selected.Name;
            LatencyText.Text = selected.LastLatencyMs >= 0
                ? $"Endpoint: {selected.Server}  •  Last measured latency: {selected.LastLatencyMs} ms"
                : $"Endpoint: {selected.Server}  •  Latency not measured yet";
        }
        else if (!preserveReferenceLocation && LocationList.SelectedItem is ServerLocation location)
        {
            ServerText.Text = location.DisplayName;
            LatencyText.Text = "Reference location only — add a real provider hostname/configuration.";
        }

        ConnectButton.Content = _vpn.IsConnected ? "DISCONNECT" : "CONNECT";
        ConnectButton.IsEnabled = !_busy;
        ConnectButton.Opacity = _busy ? 0.65 : 1;

        if (_killSwitch.IsActive)
        {
            StatusText.Text = "● KILL SWITCH LOCKDOWN";
            StatusText.Foreground = (Brush)Application.Current.Resources["Warning"];
            StatusDetailText.Text = _vpn.IsConnected ? "Tunnel connected • physical outbound traffic restricted" : "Tunnel is down • outbound traffic remains blocked";
            KillSwitchDetailText.Text = "ACTIVE: outbound rules are restricted. Uncheck this setting or use Restore firewall to restore the saved firewall state.";
        }
        else if (_vpn.IsConnected)
        {
            StatusText.Text = "● CONNECTED";
            StatusText.Foreground = (Brush)Application.Current.Resources["Green"];
            StatusDetailText.Text = $"Connected via {_vpn.ActiveProfile?.Protocol}";
            KillSwitchDetailText.Text = "Arms Windows Firewall after an OpenVPN tunnel is established. Administrator access is required.";
        }
        else if (_vpn.CurrentState.Contains("reconnect", StringComparison.OrdinalIgnoreCase))
        {
            StatusText.Text = "● RECONNECTING";
            StatusText.Foreground = (Brush)Application.Current.Resources["Warning"];
            StatusDetailText.Text = _vpn.CurrentState;
        }
        else
        {
            StatusText.Text = "● DISCONNECTED";
            StatusText.Foreground = (Brush)Application.Current.Resources["Muted"];
            StatusDetailText.Text = KillSwitchBox.IsChecked == true ? "Kill switch will arm after the next OpenVPN connection" : "Ready to connect";
        }

        var profile = ProfileBox.SelectedItem as VpnProfile;
        var ikev2Selected = ModeBox.SelectedIndex == 1 || (ModeBox.SelectedIndex == 0 && profile?.Protocol == VpnProtocol.IKEv2);
        KillSwitchBox.IsEnabled = !ikev2Selected || KillSwitchBox.IsChecked == true || _killSwitch.IsActive;
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_vpn.IsConnected)
        {
            _manualDisconnect = true;
            _busy = true;
            RefreshSelection();
            try
            {
                await _vpn.DisconnectAsync();
                if (_killSwitch.IsActive)
                    MessageBox.Show("The VPN has disconnected, but the kill switch remains active and blocks outbound traffic. Uncheck the kill switch or choose Restore firewall to restore the saved Windows Firewall state.", "Kill switch remains active", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Disconnect failed", MessageBoxButton.OK, MessageBoxImage.Warning); }
            finally { _busy = false; RefreshSelection(); }
            return;
        }

        if (_killSwitch.IsActive)
        {
            MessageBox.Show("The firewall kill switch remains in lockdown from a previous session. Restore the saved firewall state before starting a new connection.", "Firewall locked", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _busy = true;
        _manualDisconnect = false;
        RefreshSelection();
        try
        {
            var profile = await ResolveProfileAsync();
            if (profile is null)
            {
                MessageBox.Show("No matching configured profile was found. Add a real OpenVPN or Windows IKEv2 profile first.", "No configured server", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (KillSwitchBox.IsChecked == true && profile.Protocol != VpnProtocol.OpenVPN)
            {
                MessageBox.Show("The firewall kill switch currently supports OpenVPN profiles only. Choose an OpenVPN profile or uncheck the kill switch before using IKEv2.", "Kill switch is OpenVPN-only", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            await _vpn.ConnectAsync(profile);
            if (KillSwitchBox.IsChecked == true && profile.Protocol == VpnProtocol.OpenVPN)
            {
                try { await _killSwitch.ActivateAsync(profile, _vpn.OpenVpnExecutable!, _vpn.TunnelInterfaceAlias!); }
                catch (Exception ex)
                {
                    KillSwitchBox.IsChecked = false;
                    MessageBox.Show("The VPN connected, but the firewall kill switch could not be enabled. The app did not report it as active.\n\n" + ex.Message, "Kill switch not enabled", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
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
            foreach (var profile in candidates) profile.LastLatencyMs = await VpnService.PingAsync(profile.Server);
            SaveProfiles();
            candidates = candidates.OrderBy(p => p.LastLatencyMs < 0 ? int.MaxValue : p.LastLatencyMs).ToList();
        }
        var chosen = candidates.FirstOrDefault();
        if (chosen is not null) ProfileBox.SelectedItem = chosen;
        return chosen;
    }

    private void AddProfile_Click(object sender, RoutedEventArgs e)
    {
        var location = LocationList.SelectedItem as ServerLocation ?? ServerLocation.All[2];
        var dialog = new ProfileDialog(location) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Profile is null) return;
        _profiles.Add(dialog.Profile);
        ProfileBox.SelectedItem = dialog.Profile;
        SaveProfiles();
        RefreshSelection();
    }

    private void RemoveProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileBox.SelectedItem is not VpnProfile profile) return;
        if (_vpn.ActiveProfile == profile)
        {
            MessageBox.Show("Disconnect the active profile before removing it.", "Profile in use", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _profiles.Remove(profile);
        SaveProfiles();
        RefreshSelection();
    }

    private async void KillSwitchBox_Checked(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        RefreshSelection();
        if (!_vpn.IsConnected) return; // Setting is armed for the next OpenVPN connection.
        if (_vpn.ActiveProfile?.Protocol != VpnProtocol.OpenVPN)
        {
            KillSwitchBox.IsChecked = false;
            MessageBox.Show("The kill switch currently supports OpenVPN profiles only.", "OpenVPN required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try { await _killSwitch.ActivateAsync(_vpn.ActiveProfile, _vpn.OpenVpnExecutable!, _vpn.TunnelInterfaceAlias!); }
        catch (Exception ex)
        {
            KillSwitchBox.IsChecked = false;
            MessageBox.Show("The kill switch could not be enabled. Windows Firewall was not reported as protected.\n\n" + ex.Message, "Kill switch failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        RefreshSelection();
    }

    private async void KillSwitchBox_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_initializing || !_killSwitch.IsActive) { RefreshSelection(); return; }
        await RestoreKillSwitchAsync();
    }

    private async void RestoreFirewall_Click(object sender, RoutedEventArgs e)
    {
        if (!_killSwitch.IsActive)
        {
            MessageBox.Show("There is no saved kill-switch firewall state to restore.", "Firewall already restored", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await RestoreKillSwitchAsync();
    }

    private async Task RestoreKillSwitchAsync()
    {
        try
        {
            await _killSwitch.RestoreAsync();
            KillSwitchBox.IsChecked = false;
            MessageBox.Show("The saved Windows Firewall profile settings and previously enabled outbound allow rules were restored.", "Firewall restored", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("The firewall state could not be restored automatically. Keep the app open and try Restore firewall again.\n\n" + ex.Message, "Firewall restore failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { RefreshSelection(); }
    }

    private void Vpn_StateChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => Vpn_StateChanged(sender, e)));
            return;
        }
        RefreshSelection();
        if (!_vpn.IsConnected && !_manualDisconnect && ReconnectBox.IsChecked == true &&
            _vpn.ActiveProfile is { Protocol: VpnProtocol.OpenVPN } profile && !_reconnecting)
            _ = ReconnectUntilConnectedAsync(profile);
    }

    private async Task ReconnectUntilConnectedAsync(VpnProfile profile)
    {
        _reconnecting = true;
        try
        {
            while (!_manualDisconnect && !_vpn.IsConnected && ReconnectBox.IsChecked == true && _vpn.ActiveProfile is not null)
            {
                _vpn.CurrentState = "Waiting 4 seconds before reconnecting…";
                RefreshSelection();
                await Task.Delay(TimeSpan.FromSeconds(4));
                if (_manualDisconnect || ReconnectBox.IsChecked != true) break;
                try
                {
                    await _vpn.ConnectAsync(profile);
                    if (KillSwitchBox.IsChecked == true && !_killSwitch.IsActive)
                        await _killSwitch.ActivateAsync(profile, _vpn.OpenVpnExecutable!, _vpn.TunnelInterfaceAlias!);
                }
                catch (Exception ex)
                {
                    _vpn.CurrentState = "Reconnect failed: " + ex.Message;
                    RefreshSelection();
                    await Task.Delay(TimeSpan.FromSeconds(3));
                }
            }
        }
        finally { _reconnecting = false; }
    }

    private void LoadProfiles()
    {
        try
        {
            if (!File.Exists(_store)) return;
            foreach (var profile in JsonSerializer.Deserialize<List<VpnProfile>>(File.ReadAllText(_store)) ?? new())
            {
                if (string.IsNullOrWhiteSpace(profile.Region)) profile.Region = "Custom";
                _profiles.Add(profile);
            }
        }
        catch { /* Invalid local preferences should not prevent the app from opening. */ }
    }

    private void SaveProfiles()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_store)!);
            File.WriteAllText(_store, JsonSerializer.Serialize(_profiles, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            MessageBox.Show("Profiles could not be saved to your local AppData folder.\n\n" + ex.Message, "Profile save failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}

public sealed class ProfileDialog : Window
{
    public VpnProfile? Profile { get; private set; }
    private readonly TextBox _name = new();
    private readonly TextBox _server = new();
    private readonly ComboBox _protocol = new();
    private readonly ComboBox _location = new();
    private readonly TextBox _config = new();

    public ProfileDialog(ServerLocation initialLocation)
    {
        Title = "Add VPN profile";
        Width = 560; Height = 610; MinHeight = 610;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.Resources["Bg"];
        Foreground = (Brush)Application.Current.Resources["Text"];
        ResizeMode = ResizeMode.NoResize;

        _location.ItemsSource = ServerLocation.All;
        _location.DisplayMemberPath = "DisplayName";
        _location.SelectedItem = initialLocation;
        _location.SelectionChanged += (_, _) => UpdateSuggestedName();
        _protocol.ItemsSource = Enum.GetValues<VpnProtocol>().Where(p => p != VpnProtocol.Auto);
        _protocol.SelectedItem = VpnProtocol.OpenVPN;
        _name.Text = $"OpenVPN - {initialLocation.Region}";
        _config.IsReadOnly = true;

        var root = new StackPanel { Margin = new Thickness(24) };
        root.Children.Add(new TextBlock { Text = "Add a configured server", FontSize = 23, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 6) });
        root.Children.Add(new TextBlock { Text = "Reference locations are not servers. Enter a provider hostname and import its real profile.", Foreground = (Brush)Application.Current.Resources["Muted"], TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) });
        AddField(root, "Reference region", _location);
        AddField(root, "Profile name (for IKEv2, use the existing Windows VPN profile name)", _name);
        AddField(root, "VPN server hostname / IP (used for latency checks)", _server);
        AddField(root, "Protocol", _protocol);
        AddField(root, "OpenVPN configuration file (.ovpn)", _config);

        var footer = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var browse = new Button { Content = "Browse .ovpn", Width = 130 };
        browse.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Filter = "OpenVPN configuration (*.ovpn)|*.ovpn|All files (*.*)|*.*", CheckFileExists = true };
            if (dialog.ShowDialog(this) == true)
            {
                _config.Text = dialog.FileName;
                var host = VpnService.ReadFirstRemoteHost(dialog.FileName);
                if (string.IsNullOrWhiteSpace(_server.Text) && !string.IsNullOrWhiteSpace(host)) _server.Text = host;
            }
        };
        footer.Children.Add(browse);
        var add = new Button { Content = "Save profile", Width = 130, Background = (Brush)Application.Current.Resources["Accent"] };
        add.Click += Save_Click;
        Grid.SetColumn(add, 1);
        footer.Children.Add(add);
        root.Children.Add(footer);
        Content = root;
    }

    private void UpdateSuggestedName()
    {
        if (_location.SelectedItem is ServerLocation location &&
            (string.IsNullOrWhiteSpace(_name.Text) || _name.Text.StartsWith("OpenVPN - ", StringComparison.OrdinalIgnoreCase) || _name.Text.StartsWith("IKEv2 - ", StringComparison.OrdinalIgnoreCase)))
            _name.Text = $"{(_protocol.SelectedItem is VpnProtocol p ? p : VpnProtocol.OpenVPN)} - {location.Region}";
    }

    private static void AddField(Panel root, string label, Control control)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 9) };
        panel.Children.Add(new TextBlock { Text = label, Foreground = (Brush)Application.Current.Resources["Muted"], FontSize = 11, Margin = new Thickness(2, 0, 0, 3) });
        panel.Children.Add(control);
        root.Children.Add(panel);
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (_location.SelectedItem is not ServerLocation location) { MessageBox.Show("Choose a reference region."); return; }
        if (string.IsNullOrWhiteSpace(_name.Text) || string.IsNullOrWhiteSpace(_server.Text))
        {
            MessageBox.Show("Profile name and real server hostname/IP are required. Region names alone are not server addresses.");
            return;
        }
        var host = _server.Text.Trim();
        if (Uri.CheckHostName(host) != UriHostNameType.Dns && !System.Net.IPAddress.TryParse(host, out _))
        {
            MessageBox.Show("Enter a valid server hostname or IP address.");
            return;
        }
        var protocol = (VpnProtocol)(_protocol.SelectedItem ?? VpnProtocol.OpenVPN);
        if (protocol == VpnProtocol.OpenVPN && (string.IsNullOrWhiteSpace(_config.Text) || !File.Exists(_config.Text)))
        {
            MessageBox.Show("Select an existing .ovpn file for OpenVPN.");
            return;
        }
        Profile = new VpnProfile { Name = _name.Text.Trim(), Server = host, Protocol = protocol, ConfigPath = _config.Text.Trim(), Region = location.Region, LastLatencyMs = -1 };
        DialogResult = true;
        Close();
    }
}