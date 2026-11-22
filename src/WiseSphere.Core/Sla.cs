using System.Text.RegularExpressions;

namespace WiseSphere.Core;

public enum SlaKind { TimeToOwn, TimeToResolve }
public enum SlaStage { AtRisk, Breached }

/// <summary>An open ticket found by the SLA search, already classified by stage.</summary>
public sealed record SlaCandidate(int TicketId, string Name, int Priority, string EntityName, SlaKind Kind, SlaStage Stage)
{
    public string Key => $"{TicketId}:{(Kind == SlaKind.TimeToOwn ? "TTO" : "TTR")}:{(Stage == SlaStage.AtRisk ? "AT RISK" : "BREACHED")}";
}

public sealed record SlaAlert(string Key, int TicketId, Notification Notification);

/// <summary>INT-04: which SLA SMS to send - tier-aware out of hours, never twice for the same ticket/kind/stage.</summary>
public static partial class SlaDecider
{
    [GeneratedRegex(@"\b(CL[0-9]{3})\b")] private static partial Regex ClientInName();

    public static string ClientFromEntityName(string entityName)
    {
        var m = ClientInName().Match(entityName ?? "");
        return m.Success ? m.Groups[1].Value : "";
    }

    /// <summary>GLPI priority 6/5 → P1, 4 → P2, 3 → P3, else P4.</summary>
    public static int PriorityLabel(int p) => p >= 5 ? 1 : p == 4 ? 2 : p == 3 ? 3 : 4;

    /// <param name="candidates">Open tickets at risk / breached.</param>
    /// <param name="policy">Tenants, tiers and calendars.</param>
    /// <param name="alreadySent">Keys already alerted (persisted); updated in place, pruned after 3 days.</param>
    public static IReadOnlyList<SlaAlert> Decide(IEnumerable<SlaCandidate> candidates, PlatformPolicy policy,
        IDictionary<string, DateTimeOffset> alreadySent, DateTimeOffset now)
    {
        var alerts = new List<SlaAlert>();
        foreach (var c in candidates)
        {
            if (alreadySent.ContainsKey(c.Key) || alerts.Any(a => a.Key == c.Key)) continue;
            var client = ClientFromEntityName(c.EntityName);
            policy.Tenants.TryGetValue(client, out var tenant);
            var tier = tenant?.Tier ?? Tier.Silver;
            var afterHours = policy.Tiers.TryGetValue(tier, out var tp) ? tp.AfterHoursPriorities : [5, 6];
            var inHours = !policy.Calendars.TryGetValue(tenant?.Country ?? "KW", out var cal) || cal.IsOpen(now);
            if (!inHours && !afterHours.Contains(c.Priority)) continue;

            var stage = c.Stage == SlaStage.AtRisk ? "AT RISK" : "BREACHED";
            var kind = c.Kind == SlaKind.TimeToOwn ? "TTO" : "TTR";
            var msg = $"WiseSphere SLA {stage} {(client.Length > 0 ? client : "?")} {tier.ToString().ToUpperInvariant()} P{PriorityLabel(c.Priority)} {kind} #{c.TicketId}: {Text.Truncate(c.Name, 80)}";
            alerts.Add(new(c.Key, c.TicketId, new(inHours ? "desk" : "noc", msg)));
        }
        foreach (var old in alreadySent.Where(kv => now - kv.Value > TimeSpan.FromDays(3)).Select(kv => kv.Key).ToList())
            alreadySent.Remove(old);
        foreach (var a in alerts) alreadySent[a.Key] = now;
        return alerts;
    }
}

/// <summary>A GLPI satisfaction survey answer.</summary>
public sealed record SurveyAnswer(int Id, int TicketId, int? Score, string? Comment);

/// <summary>INT-03: low CSAT (≤ max score) → private service-recovery task for the Service Delivery Manager group.</summary>
public static class CsatDecider
{
    public static IReadOnlyList<GlpiWrite> Decide(IEnumerable<SurveyAnswer> answers, ISet<int> processed, int sdmGroupId, int maxScore = 2)
    {
        var writes = new List<GlpiWrite>();
        foreach (var a in answers)
        {
            if (a.Score is null || a.Score > maxScore || !processed.Add(a.Id)) continue;
            writes.Add(new("/TicketTask", ZabbixDecider.Input(
                ("tickets_id", a.TicketId), ("is_private", 1), ("state", 1), ("groups_id_tech", ZabbixDecider.NullIfZero(sdmGroupId)),
                ("content", Text.HtmlRows("Low satisfaction score – service-recovery call needed within 1 business day (SOP-05).",
                    [("Score", $"{a.Score} / 5"), ("Client comment", a.Comment)])))));
        }
        return writes;
    }
}
