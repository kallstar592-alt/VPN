# VPN Client for Windows

A Windows desktop VPN manager built with .NET 8 and WPF. The application controls installed VPN software and Windows VPN profiles; it does not operate a VPN service or provide anonymous/free VPN endpoints itself.

## Features in this build

- Dark Windows desktop interface
- Region presets for California (Los Angeles and Northern California), Texas (Dallas), Virginia (Ashburn), New York, Chicago, Seattle, Miami, and Singapore
- Clear separation between reference regions and real configured endpoints
- Auto mode measures latency only for profiles that have been configured
- OpenVPN connection-state detection: waits for OpenVPN's “Initialization Sequence Completed” message instead of treating a running process as connected
- Windows IKEv2 connection/disconnection through rasdial
- OpenVPN process-exit detection and optional reconnection attempts
- Windows Firewall kill switch for OpenVPN: saves existing outbound profile policies and enabled outbound allow-rule names, changes default outbound policy to Block, disables prior outbound allow rules, and adds scoped allows for the OpenVPN endpoint and tunnel interface
- Restore button to remove app rules and restore the saved firewall state
- GitHub Actions builds a self-contained Windows x64 package

## Locations are not live servers

The built-in locations are only region presets. They are not claims that the app has servers in those places. To connect, add a real server hostname/IP and a valid provider configuration. Auto mode compares only profiles you configured; it cannot discover or create servers.

## OpenVPN setup

1. Install OpenVPN Community Edition for Windows.
2. Download a real .ovpn profile from your VPN provider or configure one for a server you control.
3. Run this client as administrator (the app requests UAC elevation because it controls system networking/firewall settings).
4. Select a region and click Add profile for region.
5. Enter the actual endpoint hostname/IP, choose OpenVPN, and browse to the .ovpn file.
6. Click Connect.

The connection status only changes to Connected after OpenVPN reports “Initialization Sequence Completed”. Some providers also require a username/password, certificate, or separate authentication file.

## IKEv2 setup

Create and configure an IKEv2 profile in Windows first. Then add a profile in this app and use the exact Windows VPN connection name in the profile name field. The kill switch is currently OpenVPN-only and is disabled for IKEv2 use.

## Kill switch behavior and recovery

The kill switch is implemented for OpenVPN only. It is applied after a tunnel is reported established; connection setup itself is not protected by the kill switch. When activated, the app snapshots current Windows Firewall profile outbound policies and enabled outbound allow rules, changes default outbound policy to Block, disables previously enabled outbound allow rules, and adds allow rules for the configured OpenVPN endpoint and active VPN tunnel adapter.

If the VPN drops, the kill switch remains in lockdown. This is intentional fail-closed behavior: uncheck the kill-switch option or use Restore firewall / disable kill switch to restore the saved state. Keep this app available so you can run the restore action. If Windows or another security product prevents a firewall operation, the app shows an error rather than reporting protection as active.

Compatibility note: corporate Group Policy, third-party firewalls, unusual OpenVPN adapters, and provider configurations with complex multi-hop/dynamic endpoints can prevent this method from being enabled. Test carefully before relying on it for sensitive traffic. While active, the first activation changes outbound firewall defaults and disables previously enabled outbound allow rules; the prior state is saved for restoration. The app requires administrator privileges.

## Build from source

Requirements: Windows 10/11 and the .NET 8 SDK.

    dotnet restore .\VPN.csproj
    dotnet build .\VPN.csproj -c Release
    dotnet run --project .\VPN.csproj

GitHub Actions creates a self-contained VPN-Windows-x64.zip. Push a tag such as v0.2.0 to publish the package as a GitHub Release.

## Local data and secrets

Profiles are stored locally in %LOCALAPPDATA%\VPN\profiles.json. They contain local paths, server hostnames and protocol selection; credentials are not requested or stored by this app. Do not commit .ovpn files, passwords, private keys, or client certificates. Review provider configuration files before importing them.
