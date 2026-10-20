using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Net;
using System.Text;
using System.ComponentModel;

namespace VPN;

/// <summary>Windows Firewall based fail-closed mode for OpenVPN.</summary>
public sealed class KillSwitchService
{
    private const string RuleGroup = "VPN Client Kill Switch";
    private readonly string _statePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VPN", "kill-switch-firewall-state.json");
    public bool IsActive => File.Exists(_statePath);

    public async Task ActivateAsync(VpnProfile profile, string openVpnExecutable, string tunnelInterfaceAlias)
    {
        if (profile.Protocol != VpnProtocol.OpenVPN) throw new InvalidOperationException("The firewall kill switch currently supports OpenVPN profiles only.");
        if (string.IsNullOrWhiteSpace(profile.ConfigPath) || !File.Exists(profile.ConfigPath)) throw new FileNotFoundException("OpenVPN configuration is missing.", profile.ConfigPath);
        if (string.IsNullOrWhiteSpace(openVpnExecutable) || !File.Exists(openVpnExecutable)) throw new FileNotFoundException("OpenVPN executable could not be located.", openVpnExecutable);
        if (string.IsNullOrWhiteSpace(tunnelInterfaceAlias)) throw new InvalidOperationException("A matching, active OpenVPN tunnel adapter could not be identified. The kill switch was not enabled.");
        if (IsActive) return;

        var endpoints = await ResolveEndpointAddressesAsync(profile.ConfigPath);
        if (endpoints.Count == 0) throw new InvalidOperationException("No resolvable 'remote' endpoint was found in the .ovpn file.");
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);

        var state = QuotePowerShell(_statePath);
        var group = QuotePowerShell(RuleGroup);
        var exe = QuotePowerShell(Path.GetFullPath(openVpnExecutable));
        var adapter = QuotePowerShell(tunnelInterfaceAlias);
        var endpointList = QuotePowerShell(string.Join(",", endpoints));

        var script = $$$"""
$ErrorActionPreference = 'Stop'
$statePath = {{{state}}}
$group = {{{group}}}
if (Test-Path -LiteralPath $statePath) { throw 'A previous firewall snapshot exists. Restore it before enabling a new kill switch.' }
$profiles = @(Get-NetFirewallProfile | Select-Object @{Name='Name';Expression={$_.Name}}, @{Name='DefaultOutboundAction';Expression={$_.DefaultOutboundAction.ToString()}})
$rules = @(Get-NetFirewallRule -Direction Outbound -Action Allow -Enabled True | Where-Object { $_.Group -ne $group } | Select-Object -ExpandProperty Name -Unique)
$snapshot = [pscustomobject]@{ Profiles = $profiles; Rules = $rules; CreatedUtc = [DateTime]::UtcNow.ToString('o') }
$snapshot | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $statePath -Encoding UTF8
try {
    Get-NetFirewallRule -Direction Outbound -Action Allow -Enabled True | Where-Object { $_.Group -ne $group } | Disable-NetFirewallRule
    Set-NetFirewallProfile -Profile Domain,Private,Public -DefaultOutboundAction Block
    New-NetFirewallRule -DisplayName 'VPN Client Kill Switch - OpenVPN endpoint' -Group $group -Direction Outbound -Action Allow -Profile Any -Program {{{exe}}} -RemoteAddress {{{endpointList}}} | Out-Null
    New-NetFirewallRule -DisplayName 'VPN Client Kill Switch - tunnel adapter' -Group $group -Direction Outbound -Action Allow -Profile Any -InterfaceAlias {{{adapter}}} | Out-Null
}
catch {
    try {
        Get-NetFirewallRule -Group $group -ErrorAction SilentlyContinue | Remove-NetFirewallRule
        $backup = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
        foreach ($item in @($backup.Profiles)) { Set-NetFirewallProfile -Profile $item.Name -DefaultOutboundAction $item.DefaultOutboundAction }
        foreach ($name in @($backup.Rules)) { Get-NetFirewallRule -Name $name -ErrorAction SilentlyContinue | Enable-NetFirewallRule }
        Remove-Item -LiteralPath $statePath -Force -ErrorAction SilentlyContinue
    } catch { }
    throw
}
""";
        var error = await RunPowerShellAsync(script);
        if (!string.IsNullOrWhiteSpace(error)) throw new InvalidOperationException(error);
        if (!IsActive) throw new InvalidOperationException("Windows Firewall did not leave a saved kill-switch state; activation was not confirmed.");
    }

    public async Task RestoreAsync()
    {
        if (!IsActive) return;
        var state = QuotePowerShell(_statePath);
        var group = QuotePowerShell(RuleGroup);
        var script = $$$"""
$ErrorActionPreference = 'Stop'
$statePath = {{{state}}}
$group = {{{group}}}
if (-not (Test-Path -LiteralPath $statePath)) { exit 0 }
$snapshot = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
Get-NetFirewallRule -Group $group -ErrorAction SilentlyContinue | Remove-NetFirewallRule
foreach ($item in @($snapshot.Profiles)) { Set-NetFirewallProfile -Profile $item.Name -DefaultOutboundAction $item.DefaultOutboundAction }
foreach ($name in @($snapshot.Rules)) { Get-NetFirewallRule -Name $name -ErrorAction SilentlyContinue | Enable-NetFirewallRule }
Remove-Item -LiteralPath $statePath -Force
""";
        var error = await RunPowerShellAsync(script);
        if (!string.IsNullOrWhiteSpace(error)) throw new InvalidOperationException(error);
        if (IsActive) throw new InvalidOperationException("The saved firewall snapshot remains on disk, so restoration was not confirmed.");
    }

    private static async Task<List<string>> ResolveEndpointAddressesAsync(string configPath)
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in File.ReadLines(configPath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0].Equals("remote", StringComparison.OrdinalIgnoreCase)) hosts.Add(parts[1].Trim('"'));
        }

        var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var host in hosts)
        {
            try
            {
                if (IPAddress.TryParse(host, out var address)) { addresses.Add(address.ToString()); continue; }
                foreach (var resolved in await Dns.GetHostAddressesAsync(host)) addresses.Add(resolved.ToString());
            }
            catch (Exception ex) { throw new InvalidOperationException($"Could not resolve OpenVPN endpoint '{host}'. Kill switch activation was cancelled.", ex); }
        }
        return addresses.ToList();
    }

    private static string QuotePowerShell(string value) => "'" + value.Replace("'", "''") + "'";

    private static async Task<string> RunPowerShellAsync(string script)
    {
        var tempFolder = Path.Combine(Path.GetTempPath(), "VPN-KillSwitch");
        Directory.CreateDirectory(tempFolder);
        var id = Guid.NewGuid().ToString("N");
        var scriptPath = Path.Combine(tempFolder, id + ".ps1");
        await File.WriteAllTextAsync(scriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe", Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            }) ?? throw new InvalidOperationException("Could not start Windows PowerShell.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = (await stdout) + Environment.NewLine + (await stderr);
            if (process.ExitCode != 0) return string.IsNullOrWhiteSpace(output) ? $"Windows Firewall command failed with exit code {process.ExitCode}." : output.Trim();
            return string.Empty;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return "Windows elevation was cancelled. The kill switch was not enabled.";
        }
        finally { try { File.Delete(scriptPath); } catch { } }
    }
}