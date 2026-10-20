namespace VPN;

public enum VpnProtocol { Auto, IKEv2, OpenVPN }

public sealed class VpnProfile
{
    public string Name { get; set; } = "";
    public string Server { get; set; } = "";
    public string Region { get; set; } = "Custom";
    public VpnProtocol Protocol { get; set; }
    public string ConfigPath { get; set; } = "";
    public int LastLatencyMs { get; set; } = -1;
    public override string ToString() => $"{Name}  •  {Server}  •  {Protocol}";
}

public sealed class ServerLocation
{
    public string Region { get; init; } = "";
    public string City { get; init; } = "";
    public string Country { get; init; } = "United States";
    public string Description { get; init; } = "";
    public string DisplayName => $"{Region}  •  {City}";
    public override string ToString() => DisplayName;

    public static IReadOnlyList<ServerLocation> All { get; } = new List<ServerLocation>
    {
        new() { Region = "California", City = "Los Angeles", Description = "US West • Common gaming region" },
        new() { Region = "California North", City = "San Jose / Bay Area", Description = "US West • Northern California" },
        new() { Region = "Texas", City = "Dallas", Description = "US Central • Southern central US" },
        new() { Region = "Virginia", City = "Ashburn", Description = "US East • Common East Coast region" },
        new() { Region = "New York", City = "New York City", Description = "US Northeast" },
        new() { Region = "Illinois", City = "Chicago", Description = "US Central / Midwest" },
        new() { Region = "Washington", City = "Seattle", Description = "US Pacific Northwest" },
        new() { Region = "Florida", City = "Miami", Description = "US Southeast" },
        new() { Region = "Singapore", City = "Singapore", Country = "Singapore", Description = "Asia Pacific • Singapore" }
    };
}