namespace VPN;

public enum VpnProtocol { Auto, IKEv2, OpenVPN }

public sealed class VpnProfile
{
    public string Name { get; set; } = "";
    public string Server { get; set; } = "";
    public VpnProtocol Protocol { get; set; }
    public string ConfigPath { get; set; } = "";
    public int LastLatencyMs { get; set; } = -1;
    public override string ToString() => $"{Name}  •  {Server}  •  {Protocol}";
}
