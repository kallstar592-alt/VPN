using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;

namespace VPN;

public sealed class VpnService
{
    private Process? _openVpn;
    private TaskCompletionSource<bool>? _connectionReady;
    private bool _expectedExit;

    public bool IsConnected { get; private set; }
    public VpnProfile? ActiveProfile { get; private set; }
    public string CurrentState { get; set; } = "Disconnected";
    public string? OpenVpnExecutable { get; private set; }
    public string? TunnelInterfaceAlias => FindTunnelInterfaceAlias();
    public event EventHandler? StateChanged;

    public async Task ConnectAsync(VpnProfile profile, CancellationToken token = default)
    {
        if (profile.Protocol == VpnProtocol.Auto) throw new InvalidOperationException("Auto mode must select a configured profile first.");
        if (IsConnected) await DisconnectAsync();

        CurrentState = $"Connecting to {profile.Server}…";
        StateChanged?.Invoke(this, EventArgs.Empty);
        if (profile.Protocol == VpnProtocol.OpenVPN)
        {
            try { await ConnectOpenVpnAsync(profile, token); }
            catch { await StopOpenVpnAsync(); throw; }
        }
        else await ConnectIkev2Async(profile, token);

        ActiveProfile = profile;
        IsConnected = true;
        CurrentState = "Connected";
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task DisconnectAsync()
    {
        _expectedExit = true;
        try
        {
            if (ActiveProfile?.Protocol == VpnProtocol.OpenVPN || _openVpn is not null) await StopOpenVpnAsync();
            else if (ActiveProfile?.Protocol == VpnProtocol.IKEv2) await RunAsync("rasdial.exe", $"\"{ActiveProfile.Name}\" /disconnect");
        }
        finally
        {
            IsConnected = false;
            ActiveProfile = null;
            CurrentState = "Disconnected";
            _expectedExit = false;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task ConnectOpenVpnAsync(VpnProfile profile, CancellationToken token)
    {
        if (!File.Exists(profile.ConfigPath)) throw new FileNotFoundException("OpenVPN configuration was not found.", profile.ConfigPath);
        OpenVpnExecutable = FindOpenVpn();
        if (OpenVpnExecutable is null) throw new FileNotFoundException("OpenVPN Community for Windows was not found. Install it first, then retry.");

        _connectionReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _expectedExit = false;
        var psi = new ProcessStartInfo
        {
            FileName = OpenVpnExecutable,
            Arguments = $"--config \"{profile.ConfigPath}\" --verb 3 --auth-nocache",
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        _openVpn = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _openVpn.OutputDataReceived += (_, e) => HandleOpenVpnLine(e.Data);
        _openVpn.ErrorDataReceived += (_, e) => HandleOpenVpnLine(e.Data);
        _openVpn.Exited += (_, _) =>
        {
            if (_expectedExit) return;
            IsConnected = false;
            CurrentState = "OpenVPN process exited";
            StateChanged?.Invoke(this, EventArgs.Empty);
        };
        if (!_openVpn.Start()) throw new InvalidOperationException("Windows could not start OpenVPN.");
        _openVpn.BeginOutputReadLine();
        _openVpn.BeginErrorReadLine();

        var process = _openVpn;
        var exitTask = process.WaitForExitAsync(token);
        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(45), token);
        var winner = await Task.WhenAny(_connectionReady.Task, exitTask, timeoutTask);
        token.ThrowIfCancellationRequested();
        if (winner == _connectionReady.Task) { await _connectionReady.Task; return; }
        if (winner == exitTask || process.HasExited)
            throw new InvalidOperationException($"OpenVPN exited before the tunnel was established (exit code {process.ExitCode}). Check the .ovpn profile, credentials, and OpenVPN log.");
        throw new TimeoutException("OpenVPN did not report 'Initialization Sequence Completed' within 45 seconds. Check the server address, credentials, and configuration.");
    }

    private void HandleOpenVpnLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        if (line.Contains("Initialization Sequence Completed", StringComparison.OrdinalIgnoreCase))
        {
            CurrentState = "Connected";
            _connectionReady?.TrySetResult(true);
        }
        else if (line.Contains("AUTH_FAILED", StringComparison.OrdinalIgnoreCase))
        {
            CurrentState = "Authentication failed";
            _connectionReady?.TrySetException(new InvalidOperationException("OpenVPN authentication failed. Check the provider username/password or certificate configuration."));
        }
        else if (line.Contains("RECONNECTING", StringComparison.OrdinalIgnoreCase) || line.Contains("Restart pause", StringComparison.OrdinalIgnoreCase))
        {
            IsConnected = false;
            CurrentState = "Reconnecting";
        }
        else if (line.Contains("Exiting", StringComparison.OrdinalIgnoreCase) || line.Contains("process exiting", StringComparison.OrdinalIgnoreCase))
        {
            IsConnected = false;
            CurrentState = "OpenVPN exiting";
        }
        else return;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task ConnectIkev2Async(VpnProfile profile, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(profile.Name)) throw new InvalidOperationException("Enter the exact name of an existing Windows IKEv2 VPN profile.");
        var result = await RunAsync("rasdial.exe", $"\"{profile.Name}\"", token);
        if (result.ExitCode != 0) throw new InvalidOperationException(result.Output.Trim().Length > 0 ? result.Output.Trim() : "Windows could not connect to the IKEv2 profile.");
    }

    private async Task StopOpenVpnAsync()
    {
        var process = _openVpn;
        _openVpn = null;
        if (process is null) return;
        try
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
        catch { /* Continue cleanup if the process already exited. */ }
        finally { process.Dispose(); }
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

    public static string? ReadFirstRemoteHost(string configPath)
    {
        try
        {
            foreach (var rawLine in File.ReadLines(configPath))
            {
                var line = rawLine.Trim();
                if (line.StartsWith("#") || line.StartsWith(";")) continue;
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[0].Equals("remote", StringComparison.OrdinalIgnoreCase)) return parts[1].Trim('"');
            }
        }
        catch { }
        return null;
    }

    private static string? FindTunnelInterfaceAlias()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                    (HasToken(n.Name, "openvpn", "wintun", "tap", "tun", "vpn") ||
                     HasToken(n.Description, "openvpn", "wintun", "tap-windows", "tunnel")))
                .Where(n => n.GetIPProperties().UnicastAddresses.Any(a =>
                    a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address)))
                .Select(n => n.Name)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    private static bool HasToken(string value, params string[] tokens) => tokens.Any(token => value.Contains(token, StringComparison.OrdinalIgnoreCase));

    private static string? FindOpenVpn()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var candidates = new[]
        {
            Path.Combine(programFiles, "OpenVPN", "bin", "openvpn.exe"),
            Path.Combine(programFilesX86, "OpenVPN", "bin", "openvpn.exe"),
            Path.Combine(programFiles, "OpenVPN Connect", "openvpn.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string file, string args, CancellationToken token = default)
    {
        using var process = Process.Start(new ProcessStartInfo(file, args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        }) ?? throw new InvalidOperationException($"Unable to start {file}.");
        var stdout = process.StandardOutput.ReadToEndAsync(token);
        var stderr = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        return (process.ExitCode, (await stdout) + (await stderr));
    }
}