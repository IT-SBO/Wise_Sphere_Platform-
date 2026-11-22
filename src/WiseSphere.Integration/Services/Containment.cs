using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using WiseSphere.Core;
using WiseSphere.Glpi;

namespace WiseSphere.Integration.Services;

/// <summary>Firewall API keys per client (vault → environment: WS__Firewalls__CL001__ApiKey). Never in Git.</summary>
public sealed class FirewallSecrets
{
    public const string Section = "Firewalls";
    public Dictionary<string, FirewallCredential> Clients { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
public sealed class FirewallCredential { public string ApiKey { get; set; } = ""; }

/// <summary>One firewall integration. Implementations must be idempotent (blocking twice / unblocking a missing entry is not an error).</summary>
public interface IFirewallAdapter
{
    Task BlockAsync(ContainmentSettings fw, string apiKey, ActiveBlock block, CancellationToken ct);
    Task UnblockAsync(ContainmentSettings fw, string apiKey, ActiveBlock block, CancellationToken ct);
}

/// <summary>Dry run: records the decision only. Use until a client's firewall integration has been tested.</summary>
public sealed class LogOnlyAdapter(ILogger<LogOnlyAdapter> log) : IFirewallAdapter
{
    public Task BlockAsync(ContainmentSettings fw, string apiKey, ActiveBlock block, CancellationToken ct)
    { log.LogWarning("LOG-ONLY containment: would block {Ip} for {Client} until {Expires}", block.Ip, block.ClientCode, block.ExpiresAt); return Task.CompletedTask; }
    public Task UnblockAsync(ContainmentSettings fw, string apiKey, ActiveBlock block, CancellationToken ct)
    { log.LogWarning("LOG-ONLY containment: would unblock {Ip} for {Client}", block.Ip, block.ClientCode); return Task.CompletedTask; }
}

/// <summary>Generic HTTP contract for a client-side middleware or the lab mock:
/// POST {url}/block {ip, expires_at, comment}; DELETE {url}/block/{ip}. Bearer key.</summary>
public sealed class GenericHttpAdapter(HttpClient http) : IFirewallAdapter
{
    public async Task BlockAsync(ContainmentSettings fw, string apiKey, ActiveBlock block, CancellationToken ct)
    {
        using var req = Req(HttpMethod.Post, $"{fw.FirewallUrl.TrimEnd('/')}/block", apiKey,
            new { ip = block.Ip, expires_at = block.ExpiresAt, comment = $"WiseSphere ticket {block.TicketId} approved by {block.ApprovedBy}" });
        using var res = await http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
    }
    public async Task UnblockAsync(ContainmentSettings fw, string apiKey, ActiveBlock block, CancellationToken ct)
    {
        using var req = Req(HttpMethod.Delete, $"{fw.FirewallUrl.TrimEnd('/')}/block/{Uri.EscapeDataString(block.Ip)}", apiKey, null);
        using var res = await http.SendAsync(req, ct);
        if (res.StatusCode != System.Net.HttpStatusCode.NotFound) res.EnsureSuccessStatusCode();
    }
    internal static HttpRequestMessage Req(HttpMethod m, string url, string key, object? body)
    {
        var r = new HttpRequestMessage(m, url);
        if (key.Length > 0) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (body is not null) r.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return r;
    }
}

/// <summary>FortiGate (FortiOS REST API): an address object per blocked IP, member of the address group used by a deny policy
/// that the client's firewall team created once (e.g. "WS-Blocklist" in a deny rule at the top). Verify on the pilot's FortiOS version.</summary>
public sealed class FortiGateAdapter(HttpClient http) : IFirewallAdapter
{
    private static string Name(ActiveBlock b) => $"ws-block-{b.Ip.Replace(':', '-')}";

    public async Task BlockAsync(ContainmentSettings fw, string apiKey, ActiveBlock block, CancellationToken ct)
    {
        var api = fw.FirewallUrl.TrimEnd('/') + "/api/v2/cmdb/firewall";
        var mask = block.Ip.Contains(':', StringComparison.Ordinal) ? "/128" : " 255.255.255.255";
        using (var res = await http.SendAsync(GenericHttpAdapter.Req(HttpMethod.Post, $"{api}/address", apiKey,
                   new { name = Name(block), subnet = block.Ip + mask, comment = $"WiseSphere ticket {block.TicketId}, expires {block.ExpiresAt:u}" }), ct))
            if (!res.IsSuccessStatusCode && (int)res.StatusCode != 500) res.EnsureSuccessStatusCode(); // 500 = already exists on some versions
        using var add = await http.SendAsync(GenericHttpAdapter.Req(HttpMethod.Post, $"{api}/addrgrp/{Uri.EscapeDataString(fw.AddressGroup)}/member", apiKey,
            new { name = Name(block) }), ct);
        add.EnsureSuccessStatusCode();
    }

    public async Task UnblockAsync(ContainmentSettings fw, string apiKey, ActiveBlock block, CancellationToken ct)
    {
        var api = fw.FirewallUrl.TrimEnd('/') + "/api/v2/cmdb/firewall";
        using (var res = await http.SendAsync(GenericHttpAdapter.Req(HttpMethod.Delete, $"{api}/addrgrp/{Uri.EscapeDataString(fw.AddressGroup)}/member/{Name(block)}", apiKey, null), ct))
            if (res.StatusCode != System.Net.HttpStatusCode.NotFound) res.EnsureSuccessStatusCode();
        using var del = await http.SendAsync(GenericHttpAdapter.Req(HttpMethod.Delete, $"{api}/address/{Name(block)}", apiKey, null), ct);
        if (del.StatusCode != System.Net.HttpStatusCode.NotFound) del.EnsureSuccessStatusCode();
    }
}

public sealed record ContainmentResult(bool Ok, IReadOnlyList<string> Errors, ActiveBlock? Block);

/// <summary>SOC-04 approved containment: validate → firewall → GLPI follow-up → remember for expiry.</summary>
public sealed class ContainmentService(
    PlatformPolicy policy, StateStore state, GlpiClient glpi, IOptions<FirewallSecrets> secrets, Metrics metrics,
    LogOnlyAdapter logOnly, GenericHttpAdapter generic, FortiGateAdapter fortigate, ILogger<ContainmentService> log)
{
    private const string StateName = "containment-blocks";
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IFirewallAdapter Adapter(ContainmentSettings fw) => fw.FirewallType switch
    {
        "fortigate" => fortigate,
        "generic-http" => generic,
        _ => logOnly,
    };
    private string Key(string client) => secrets.Value.Clients.TryGetValue(client, out var c) ? c.ApiKey : "";
    private ContainmentSettings? Settings(string client) => policy.Containment?.GetValueOrDefault(client);

    public async Task<IReadOnlyList<ActiveBlock>> ListAsync(int? ticketId, CancellationToken ct) =>
        (await state.LoadAsync<List<ActiveBlock>>(StateName, ct)).Where(b => ticketId is null || b.TicketId == ticketId).ToList();

    public async Task<ContainmentResult> BlockAsync(BlockRequest r, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var blocks = await state.LoadAsync<List<ActiveBlock>>(StateName, ct);
            var fw = Settings(r.ClientCode);
            var errors = ContainmentPolicy.Validate(r, fw, blocks);
            if (errors.Count > 0) return new(false, errors, null);

            var now = DateTimeOffset.UtcNow;
            var block = new ActiveBlock(Convert.ToHexString(RandomNumberGenerator.GetBytes(6)), r.TicketId, r.ClientCode,
                System.Net.IPAddress.Parse(r.Ip).ToString(), now, now.AddHours(r.Hours), r.ApprovedBy, r.Reason.Trim(), fw!.FirewallType);
            await Adapter(fw).BlockAsync(fw, Key(r.ClientCode), block, ct);
            blocks.Add(block);
            await state.SaveAsync(StateName, blocks, ct);
            await Followup(block.TicketId, $"Containment APPROVED by {block.ApprovedBy}: {block.Ip} blocked on the {fw.FirewallType} firewall " +
                $"until {block.ExpiresAt:yyyy-MM-dd HH:mm} UTC (auto-expiry). Reason: {block.Reason}", ct);
            metrics.Inc("ws_containment_blocks_total", fw.FirewallType);
            log.LogWarning("Containment: {Ip} blocked for {Client} (ticket {Ticket}) by {Approver} until {Expires}",
                block.Ip, block.ClientCode, block.TicketId, block.ApprovedBy, block.ExpiresAt);
            return new(true, [], block);
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> UnblockAsync(string id, string by, string reason, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var blocks = await state.LoadAsync<List<ActiveBlock>>(StateName, ct);
            var b = blocks.FirstOrDefault(x => x.Id == id);
            if (b is null) return false;
            await RemoveAsync(b, $"Containment REMOVED by {by}: {b.Ip} unblocked. {reason}".Trim(), ct);
            blocks.Remove(b);
            await state.SaveAsync(StateName, blocks, ct);
            return true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Called by the scheduler: removes expired blocks (SOC-04 auto-expiry).</summary>
    public async Task<int> ExpireAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var blocks = await state.LoadAsync<List<ActiveBlock>>(StateName, ct);
            var expired = ContainmentPolicy.Expired(blocks, DateTimeOffset.UtcNow);
            foreach (var b in expired)
            {
                await RemoveAsync(b, $"Containment EXPIRED: {b.Ip} unblocked automatically after the approved period.", ct);
                blocks.Remove(b);
            }
            if (expired.Count > 0) await state.SaveAsync(StateName, blocks, ct);
            return expired.Count;
        }
        finally { _gate.Release(); }
    }

    private async Task RemoveAsync(ActiveBlock b, string note, CancellationToken ct)
    {
        var fw = Settings(b.ClientCode) ?? new ContainmentSettings(true, b.FirewallType, "", "WS-Blocklist", []);
        await Adapter(fw).UnblockAsync(fw, Key(b.ClientCode), b, ct);
        await Followup(b.TicketId, note, ct);
        log.LogWarning("Containment: {Ip} unblocked for {Client} (ticket {Ticket})", b.Ip, b.ClientCode, b.TicketId);
    }

    private Task<int> Followup(int ticketId, string text, CancellationToken ct) =>
        glpi.CreateAsync(new GlpiWrite("/ITILFollowup", new Dictionary<string, object?>
        {
            ["itemtype"] = "Ticket", ["items_id"] = ticketId, ["is_private"] = 1,
            ["content"] = Text.HtmlRows(text, []),
        }), ct);
}
