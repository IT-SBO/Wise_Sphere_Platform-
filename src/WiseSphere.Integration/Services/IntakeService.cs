using System.Diagnostics;
using Microsoft.Extensions.Options;
using WiseSphere.Core;
using WiseSphere.Glpi;

namespace WiseSphere.Integration.Services;

/// <summary>Result returned to the webhook caller (and visible in logs).</summary>
public sealed record IntakeResult(bool Ok, string Action, string? ClientCode, int EntityId, bool Mapped, int? TicketId, bool Paged, string? Reason);

/// <summary>Runs INT-01 (Zabbix) and INT-02 (Wazuh): gather GLPI context, decide, write, page.</summary>
public sealed class IntakeService(
    GlpiClient glpi, GlpiCatalog catalog, KeyedLock locks, SmsSender sms, Metrics metrics,
    IOptions<IntegrationOptions> options, ILogger<IntakeService> log)
{
    private readonly IntegrationOptions _o = options.Value;

    public async Task<IntakeResult> HandleZabbixAsync(ZabbixEvent ev, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        // Same client => strictly in order (dedupe + storm counting stay consistent). Other clients run in parallel.
        using var _ = await locks.AcquireAsync("zbx:" + (ev.ClientCode.Length > 0 ? ev.ClientCode : "UNMAPPED"), ct);
        var ids = await catalog.GetAsync(ct);
        var entityId = await glpi.FindEntityIdByTagAsync(ev.ClientCode, ct);
        var eventTickets = await glpi.FindTicketsByNameAsync(ev.Key, ct);
        IReadOnlyList<TicketRef> storm = [];
        var recent = 0;
        if (ev.Status != ZabbixStatus.Update)
            storm = await glpi.FindTicketsByNameAsync(ZabbixDecider.StormKey(ev.ClientCode), ct);
        if (ev.Status == ZabbixStatus.Problem && entityId is not null && eventTickets.Count == 0)
            recent = await glpi.CountRecentTicketsAsync(entityId.Value, "[ZBX:", _o.StormWindowMinutes, ct);

        var d = ZabbixDecider.Decide(ev, entityId, eventTickets, storm, recent,
            new(_o.DefaultEntityId, _o.StormThreshold, ids.Monitoring, ids.Noc, ids.SolutionType));
        var result = await ApplyAsync(d, ev.ClientCode, entityId, ct);
        metrics.Inc("ws_zabbix_events_total", d.Action);
        metrics.Observe("ws_zabbix_event_processing", sw.Elapsed);
        log.LogInformation("Zabbix event {EventId} {Status} client={Client} -> {Action} ticket={Ticket} in {Ms} ms",
            ev.EventId, ev.Status, ev.ClientCode, d.Action, result.TicketId, sw.ElapsedMilliseconds);
        return result;
    }

    public async Task<IntakeResult> HandleSecurityAsync(SecurityAlert a, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var _ = await locks.AcquireAsync("wzh:" + (a.ClientCode.Length > 0 ? a.ClientCode : "UNMAPPED"), ct);
        var ids = await catalog.GetAsync(ct);
        var entityId = await glpi.FindEntityIdByTagAsync(a.ClientCode, ct);
        var tickets = a.Level >= _o.WazuhTicketLevel ? await glpi.FindTicketsByNameAsync(a.Key, ct) : [];
        var d = SecurityDecider.Decide(a, entityId, tickets,
            new(_o.DefaultEntityId, ids.Security, ids.Soc, _o.WazuhTicketLevel, _o.WazuhPageLevel));
        var result = await ApplyAsync(d, a.ClientCode, entityId, ct);
        metrics.Inc("ws_security_alerts_total", d.Action);
        metrics.Observe("ws_security_alert_processing", sw.Elapsed);
        log.LogInformation("Wazuh alert {Rule} L{Level} agent={Agent} client={Client} -> {Action} ticket={Ticket}",
            a.RuleId, a.Level, a.AgentId, a.ClientCode, d.Action, result.TicketId);
        return result;
    }

    private async Task<IntakeResult> ApplyAsync(Decision d, string client, int? entityId, CancellationToken ct)
    {
        int? ticketId = d.TicketId;
        foreach (var w in d.Writes)
        {
            var id = await glpi.CreateAsync(w, ct);
            if (w.Path == "/Ticket") ticketId = id;
        }
        var paged = d.Notify is not null && await sms.SendAsync(d.Notify, ct);
        return new(true, d.Action, client.Length > 0 ? client : null, entityId ?? _o.DefaultEntityId, entityId is not null, ticketId, paged, d.Reason);
    }
}
