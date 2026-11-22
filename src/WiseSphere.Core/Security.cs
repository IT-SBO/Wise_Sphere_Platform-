using System.Text.Json;

namespace WiseSphere.Core;

/// <summary>Normalised Wazuh alert (contract ws.secevent.v1, schemas/ws.secevent.v1.json).</summary>
public sealed record SecurityAlert
{
    public string AlertId { get; init; } = "";
    public string Timestamp { get; init; } = "";
    public required string RuleId { get; init; }
    public required int Level { get; init; }
    public required string Description { get; init; }
    public IReadOnlyList<string> Groups { get; init; } = [];
    public string Mitre { get; init; } = "";
    public required string AgentId { get; init; }
    public string AgentName { get; init; } = "";
    public string AgentIp { get; init; } = "";
    public string ClientCode { get; init; } = "";
    public string SiteCode { get; init; } = "";
    public string SourceIp { get; init; } = "";
    public string FullLog { get; init; } = "";
    public string Key => $"[WZH:{AgentId}:{RuleId}]";
}

public static class SecurityAlertParser
{
    private static readonly string[] MitreFields = ["id", "tactic"];

    public static ParseResult<SecurityAlert> Parse(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) return new(null, ["body is not a JSON object"]);
        var rule = Json.Obj(body, "rule");
        var agent = Json.Obj(body, "agent");
        var labels = Json.Obj(agent, "labels");
        var errors = new List<string>();

        var ruleId = Text.Clean(Json.Str(rule, "id"));
        var level = Json.Number(rule, "level");
        var description = Text.Clean(Json.Str(rule, "description"));
        var agentId = Text.Clean(Json.Str(agent, "id"));
        var client = Text.Clean(Json.Str(labels, "client")).ToUpperInvariant();
        if (ruleId.Length == 0) errors.Add("rule.id is required");
        if (level is null) errors.Add("rule.level must be a number");
        if (agentId.Length == 0) errors.Add("agent.id is required");
        if (description.Length == 0) errors.Add("rule.description is required");
        if (client.Length > 0 && !ZabbixEventParser.ClientCodeRx().IsMatch(client)) errors.Add("agent.labels.client must look like CL001");
        if (errors.Count > 0) return new(null, errors);

        var groups = rule.TryGetProperty("groups", out var g) && g.ValueKind == JsonValueKind.Array
            ? g.EnumerateArray().Select(x => x.ToString()).ToList() : [];
        var mitre = Json.Obj(rule, "mitre");
        var mitreText = string.Join(", ", MitreFields
            .SelectMany(k => mitre.ValueKind == JsonValueKind.Object && mitre.TryGetProperty(k, out var a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray().Select(x => x.ToString()) : []));

        return new(new SecurityAlert
        {
            AlertId = Text.Clean(Json.Str(body, "alert_id") ?? Json.Str(body, "id")),
            Timestamp = Text.Clean(Json.Str(body, "timestamp")),
            RuleId = ruleId,
            Level = level!.Value,
            Description = description,
            Groups = groups,
            Mitre = mitreText,
            AgentId = agentId,
            AgentName = Text.Clean(Json.Str(agent, "name")),
            AgentIp = Text.Clean(Json.Str(agent, "ip")),
            ClientCode = client,
            SiteCode = Text.Clean(Json.Str(labels, "site")).ToUpperInvariant(),
            SourceIp = Text.Clean(Json.Str(body, "srcip") ?? Json.Str(Json.Obj(body, "data"), "srcip")),
            FullLog = Text.Truncate(Text.Clean(Json.Str(body, "full_log")), 2000),
        }, []);
    }
}

public sealed record SecurityDecisionSettings(
    int DefaultEntityId = 0,
    int RequestTypeId = 0,
    int SocGroupId = 0,
    int TicketLevel = 10,
    int PageLevel = 12);

/// <summary>INT-02: Wazuh alert → SOC ticket (dedupe on agent+rule while open); page SOC at level ≥ PageLevel. No automatic blocking (C-02).</summary>
public static class SecurityDecider
{
    public static (int Urgency, int Impact) MapLevel(int level) => level switch
    {
        >= 13 => (5, 5),
        12 => (4, 4),
        >= 10 => (4, 3),
        _ => (3, 2),
    };

    public static Decision Decide(SecurityAlert a, int? entityId, IReadOnlyList<TicketRef> alertTickets, SecurityDecisionSettings s)
    {
        if (a.Level < s.TicketLevel) return Decision.Noop($"level below {s.TicketLevel}");
        var entity = entityId ?? s.DefaultEntityId;
        var rows = new List<(string, string?)>
        {
            ("Client", a.ClientCode.Length > 0 ? a.ClientCode : "UNMAPPED"), ("Site", a.SiteCode),
            ("Agent", $"{a.AgentName} (#{a.AgentId})"), ("Agent IP", a.AgentIp),
            ("Rule", $"{a.RuleId} – {a.Description}"), ("Level", a.Level.ToString()), ("Groups", string.Join(", ", a.Groups)),
            ("MITRE", a.Mitre), ("Source IP", a.SourceIp), ("Time", a.Timestamp), ("Wazuh alert ID", a.AlertId), ("Log", a.FullLog),
        };

        var existing = Tickets.FindByKey(alertTickets, a.Key, openOnly: true);
        if (existing is not null)
        {
            var fu = ZabbixDecider.Input(("itemtype", "Ticket"), ("items_id", existing.Id), ("is_private", 1),
                ("requesttypes_id", ZabbixDecider.NullIfZero(s.RequestTypeId)),
                ("content", Text.HtmlRows("Repeat security alert (same agent and rule).", rows)));
            return new("followup", [new("/ITILFollowup", fu)], null, existing.Id);
        }

        var (urgency, impact) = MapLevel(a.Level);
        var input = ZabbixDecider.Input(
            ("name", Text.Truncate($"{a.Key} {a.AgentName}: {a.Description}", 250)), ("entities_id", entity), ("type", 1),
            ("status", TicketStatus.New), ("urgency", urgency), ("impact", impact),
            ("requesttypes_id", ZabbixDecider.NullIfZero(s.RequestTypeId)), ("_groups_id_assign", ZabbixDecider.NullIfZero(s.SocGroupId)),
            ("content", Text.HtmlRows("Security alert raised by Wazuh. Containment needs SOC approval in this ticket (SOP-03).", rows,
                "Opened automatically by WiseSphere (Wazuh → GLPI).")));
        Notification? page = a.Level >= s.PageLevel
            ? new("soc", $"WiseSphere SEC L{a.Level} {(a.ClientCode.Length > 0 ? a.ClientCode : "UNMAPPED")}: {Text.Truncate($"{a.AgentName} – {a.Description}", 110)}")
            : null;
        return new("create", [new("/Ticket", input)], page);
    }
}
