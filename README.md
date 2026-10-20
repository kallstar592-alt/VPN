# VPN Client for Windows

A Windows desktop VPN client built with .NET 8 + WPF. It provides a single interface for managing compatible VPN configurations rather than implementing a VPN protocol from scratch.

## Features

- One-click VPN ON/OFF control
- Auto mode: choose the lowest-latency configured server
- OpenVPN `.ovpn` profiles
- Windows IKEv2 profiles via `rasdial`
- Server/profile manager
- Kill-switch setting placeholder for the Windows filtering layer
- Auto-reconnect setting
- Dark desktop UI
- GitHub Actions build

## Important

This application does **not** provide VPN servers or bypass authentication. A connection requires a server and credentials/configuration from a VPN provider or your own VPN server.

### OpenVPN

Install OpenVPN for Windows, then import an `.ovpn` profile into the app. The app launches the installed OpenVPN executable with that profile.

### IKEv2

Create/configure an IKEv2 VPN connection in Windows first. Enter its Windows VPN profile name in the app and the client uses `rasdial` to connect/disconnect it.

## Build

Requirements:

- Windows 10/11
- .NET 8 SDK
- OpenVPN installed if OpenVPN mode is used

```powershell
dotnet restore
dotnet build -c Release
dotnet run
```

The publish workflow produces a Windows x64 build from GitHub Actions.

## Security

Do not commit passwords, private keys, certificates, or `.ovpn` files containing secrets. Keep those files local and add them to `.gitignore` where appropriate.
