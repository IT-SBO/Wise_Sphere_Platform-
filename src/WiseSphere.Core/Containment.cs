using System.Net;
using System.Net.Sockets;

namespace WiseSphere.Core;

/// <summary>Per-client containment settings (config/tenants.yaml "containment"). Blocking is opt-in by contract (C-02, §11.2).</summary>
public sealed record ContainmentSettings(
    bool Enabled,
    string FirewallType,          // fortigate | generic-http | log-only
    string FirewallUrl,
    string AddressGroup,
    IReadOnlyList<string> AllowList,
    int MaxHours = 24);

public sealed record BlockRequest(int TicketId, string ClientCode, string Ip, int Hours, string ApprovedBy, string Reason);

public sealed record ActiveBlock(string Id, int TicketId, string ClientCode, string Ip, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt,
    string ApprovedBy, string Reason, string FirewallType);

/// <summary>SOC-04 rules: containment only after human approval, only for opted-in clients, never on allow-listed or
/// internal addresses, always with an expiry (max 24 h). Pure logic - the integration service executes the result.</summary>
public static class ContainmentPolicy
{
    public static IReadOnlyList<string> Validate(BlockRequest r, ContainmentSettings? settings, IEnumerable<ActiveBlock> active)
    {
        var errors = new List<string>();
        if (settings is null || !settings.Enabled) { errors.Add($"containment is not enabled for {r.ClientCode} (contract opt-in required)"); return errors; }
        if (r.TicketId <= 0) errors.Add("a security ticket is required");
        if (string.IsNullOrWhiteSpace(r.ApprovedBy)) errors.Add("the approving SOC engineer is required");
        if (string.IsNullOrWhiteSpace(r.Reason) || r.Reason.Trim().Length < 5) errors.Add("a reason is required");
        if (r.Hours < 1 || r.Hours > settings.MaxHours) errors.Add($"duration must be 1-{settings.MaxHours} hours");

        if (!IPAddress.TryParse(r.Ip, out var ip) || ip.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
        {
            errors.Add("a valid IP address is required");
            return errors;
        }
        if (!IsPublic(ip)) errors.Add("internal, loopback and reserved addresses are never blocked");
        if (settings.AllowList.Any(c => InCidr(ip, c))) errors.Add("the address is on the client's allow-list");
        if (active.Any(b => b.ClientCode == r.ClientCode && b.Ip == ip.ToString())) errors.Add("the address is already blocked for this client");
        return errors;
    }

    /// <summary>Blocks whose expiry has passed (the expiry job removes them from the firewall).</summary>
    public static IReadOnlyList<ActiveBlock> Expired(IEnumerable<ActiveBlock> active, DateTimeOffset now) =>
        active.Where(b => b.ExpiresAt <= now).ToList();

    public static bool IsPublic(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return false;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return !(ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || ip.Equals(IPAddress.IPv6Any) || InCidr(ip, "fc00::/7"));
        string[] reserved = ["0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16", "172.16.0.0/12",
            "192.0.0.0/24", "192.168.0.0/16", "198.18.0.0/15", "224.0.0.0/4", "240.0.0.0/4"];
        return !reserved.Any(c => InCidr(ip, c));
    }

    public static bool InCidr(IPAddress ip, string cidr)
    {
        var parts = cidr.Split('/');
        if (!IPAddress.TryParse(parts[0], out var net) || net.AddressFamily != ip.AddressFamily) return false;
        var bits = parts.Length > 1 && int.TryParse(parts[1], out var b) ? b : (ip.AddressFamily == AddressFamily.InterNetwork ? 32 : 128);
        var a = ip.GetAddressBytes();
        var n = net.GetAddressBytes();
        for (var i = 0; i < a.Length && bits > 0; i++, bits -= 8)
        {
            var mask = bits >= 8 ? 0xFF : (byte)(0xFF << (8 - bits));
            if ((a[i] & mask) != (n[i] & mask)) return false;
        }
        return true;
    }
}
