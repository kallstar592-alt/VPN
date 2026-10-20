using System.Diagnostics;
using System.Net.NetworkInformation;

namespace VPN;

public sealed class VpnService
{
    private Process? _openVpn;
    public bool IsConnected { get; private set; }
    public VpnProfile? ActiveProfile { get; private set; }

    public async Task ConnectAsync(VpnProfile profile, CancellationToken token = default)
    {
        if (profile.Protocol == VpnProtocol.Auto) throw new InvalidOperationException("Auto must resolve to a concrete profile first.");
        if (profile.Protocol == VpnProtocol.OpenVPN) await ConnectOpenVpnAsync(profile, token);
        else await ConnectIkev2Async(profile, token);
        ActiveProfile = profile;
        IsConnected = true;
    }

    public async Task DisconnectAsync()
    {
        if (ActiveProfile?.Protocol == VpnProtocol.OpenVPN)
        {
            try { if (_openVpn is { HasExited: false }) { _openVpn.Kill(true); await _openVpn.WaitForExitAsync(); } } catch { }
            _openVpn?.Dispose(); _openVpn = null;
        }
        else if (ActiveProfile?.Protocol == VpnProtocol.IKEv2)
        {
            await RunAsync("rasdial.exe", $"\"{ActiveProfile.Name}\" /disconnect");
        }
        ActiveProfile = null;
        IsConnected = false;
    }

    private async Task ConnectOpenVpnAsync(VpnProfile profile, CancellationToken token)
    {
        if (!File.Exists(profile.ConfigPath)) throw new FileNotFoundException("OpenVPN configuration was not found.", profile.ConfigPath);
        var exe = FindOpenVpn();
        if (exe is null) throw new FileNotFoundException("OpenVPN was not found. Install OpenVPN for Windows first.");
        var psi = new ProcessStartInfo(exe, $"--config \"{profile.ConfigPath}\" --verb 3") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        _openVpn = Process.Start(psi) ?? throw new InvalidOperationException("Could not start OpenVPN.");
        var start = DateTime.UtcNow;
        while (!_openVpn.HasExited && DateTime.UtcNow - start < TimeSpan.FromSeconds(20))
        {
            token.ThrowIfCancellationRequested();
            await Task.Delay(250, token);
            if (_openVpn.HasExited) break;
            // A running OpenVPN process is not proof of an established tunnel; production builds should parse its management/log state.
            if (DateTime.UtcNow - start > TimeSpan.FromSeconds(3)) return;
        }
        if (_openVpn.HasExited) throw new InvalidOperationException("OpenVPN exited before the tunnel could be established.");
    }

    private static async Task ConnectIkev2Async(VpnProfile profile, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(profile.Name)) throw new InvalidOperationException("Enter the Windows IKEv2 VPN profile name.");
        var result = await RunAsync("rasdial.exe", $"\"{profile.Name}\"", token);
        if (result.ExitCode != 0) throw new InvalidOperationException(result.Output.Trim().Length > 0 ? result.Output.Trim() : "Windows could not connect to the IKEv2 profile.");
    }

    public static async Task<int> PingAsync(string host)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, 1500);
            return reply.Status == IPStatus.Success ? (int)reply.RoundtripTime : -1;
        }
        catch { return -1; }
    }

    private static string? FindOpenVpn()
    {
        var candidates = new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenVPN", "bin", "openvpn.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "OpenVPN", "bin", "openvpn.exe") };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string file, string args, CancellationToken token = default)
    {
        using var p = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }) ?? throw new InvalidOperationException($"Unable to start {file}.");
        var outputTask = p.StandardOutput.ReadToEndAsync(token);
        var errorTask = p.StandardError.ReadToEndAsync(token);
        await p.WaitForExitAsync(token);
        return (p.ExitCode, (await outputTask) + (await errorTask));
    }
}
