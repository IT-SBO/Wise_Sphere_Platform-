using System.Text.Json;
using System.Text.RegularExpressions;

namespace WiseSphere.Core;

public enum ZabbixStatus { Problem, Resolved, Update }

/// <summary>Normalised Zabbix event (contract ws.event.v1, schemas/ws.event.v1.json).</summary>
public sealed record ZabbixEvent
{
    public required string EventId { get; init; }
    public required ZabbixStatus Status { get; init; }
    public int Severity { get; init; }
    public required string EventName { get; init; }
    public required string Host { get; init; }
    public string HostName { get; init; } = "";
    public string HostIp { get; init; } = "";
    public string ClientCode { get; init; } = "";
    public string SiteCode { get; init; } = "";
    /// <summary>GLPI asset as itemtype:id, e.g. NetworkEquipment:12 (ITAM-05).</summary>
    public string GlpiAsset { get; init; } = "";
    public string EventTime { get; init; } = "";
    public string OpData { get; init; } = "";
    public string EventUrl { get; init; } = "";
    public string UpdateMessage { get; init; } = "";
    public string UpdateUser { get; init; } = "";

    public static readonly string[] SeverityLabels = ["Not classified", "Information", "Warning", "Average", "High", "Disaster"];
    public string SeverityLabel => Severity is >= 0 and <= 5 ? SeverityLabels[Severity] : Severity.ToString();
    public string Key => $"[ZBX:{EventId}]";
}

public sealed record ParseResult<T>(T? Value, IReadOnlyList<string> Errors) where T : class
{
    public bool IsValid => Errors.Count == 0 && Value is not null;
}

public static partial class ZabbixEventParser
{
    [GeneratedRegex("^CL[0-9]{3}$")] internal static partial Regex ClientCodeRx();
    [GeneratedRegex("^[A-Za-z]+:[0-9]+$")] private static partial Regex AssetRx();
    [GeneratedRegex("^[0-9]+$")] private static partial Regex DigitsRx();

    /// <summary>Validate a webhook body. Accepts ws.event.v1 and the PoC payload (subject/eventid/severity label).</summary>
    public static ParseResult<ZabbixEvent> Parse(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) return new(null, ["body is not a JSON object"]);
        var tags = Json.ParseTags(Json.Str(body, "tags"));
        string Get(params string[] names) => names.Select(n => Text.Clean(Json.Str(body, n))).FirstOrDefault(v => v.Length > 0) ?? "";

        var statusRaw = Get("status").ToUpperInvariant();
        var value = Get("event_value");
        if (statusRaw.Length == 0 && value.Length > 0) statusRaw = value == "0" ? "RESOLVED" : "PROBLEM";
        if (statusRaw is "OK" or "RECOVERY") statusRaw = "RESOLVED";
        if (Get("event_update") == "1" && statusRaw == "PROBLEM") statusRaw = "UPDATE";

        var errors = new List<string>();
        var eventId = Get("event_id", "eventid");
        if (!DigitsRx().IsMatch(eventId)) errors.Add("event_id must be numeric");
        ZabbixStatus? status = statusRaw switch
        {
            "PROBLEM" => ZabbixStatus.Problem, "RESOLVED" => ZabbixStatus.Resolved, "UPDATE" => ZabbixStatus.Update, _ => null,
        };
        if (status is null) errors.Add("status must be PROBLEM, RESOLVED or UPDATE");

        var sevRaw = Get("severity");
        var severity = -1;
        if (sevRaw.Length == 1 && sevRaw[0] is >= '0' and <= '5') severity = sevRaw[0] - '0';
        else
        {
            var idx = Array.FindIndex(ZabbixEvent.SeverityLabels, l => l.Equals(sevRaw, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) severity = idx;
        }
        if (status == ZabbixStatus.Problem && severity < 0) errors.Add("severity must be 0-5");

        var name = Get("event_name", "subject");
        if (name.Length == 0) errors.Add("event_name is required");
        var host = Get("host");
        if (host.Length == 0) errors.Add("host is required");

        var client = (Get("client_code").Length > 0 ? Get("client_code") : Text.Clean(tags.GetValueOrDefault("client"))).ToUpperInvariant();
        if (client.Length > 0 && !ClientCodeRx().IsMatch(client)) errors.Add("client_code must look like CL001");
        var asset = Get("glpi_asset").Length > 0 ? Get("glpi_asset") : Text.Clean(tags.GetValueOrDefault("glpi_asset"));
        if (!AssetRx().IsMatch(asset)) asset = "";

        if (errors.Count > 0) return new(null, errors);
        return new(new ZabbixEvent
        {
            EventId = eventId,
            Status = status!.Value,
            Severity = Math.Max(severity, 0),
            EventName = name,
            Host = host,
            HostName = Get("host_name").Length > 0 ? Get("host_name") : host,
            HostIp = Get("host_ip"),
            ClientCode = client,
            SiteCode = (Get("site_code").Length > 0 ? Get("site_code") : Text.Clean(tags.GetValueOrDefault("site"))).ToUpperInvariant(),
            GlpiAsset = asset,
            EventTime = Get("event_time"),
            OpData = Get("opdata"),
            EventUrl = Get("event_url"),
            UpdateMessage = Get("update_message"),
            UpdateUser = Get("update_user"),
        }, []);
    }
}

/// <summary>Settings the Zabbix decider needs (GLPI ids are resolved by name at start-up).</summary>
public sealed record ZabbixDecisionSettings(
    int DefaultEntityId = 0,
    int StormThreshold = 10,
    int RequestTypeId = 0,
    int NocGroupId = 0,
    int SolutionTypeId = 0);

/// <summary>INT-01: decides what to write to GLPI for one Zabbix event.</summary>
public static class ZabbixDecider
{
    private const string Footer = "Opened automatically by WiseSphere (Zabbix → GLPI).";

    /// <summary>INT-01 mapping: Zabbix severity → GLPI urgency/impact (GLPI computes priority from its matrix).</summary>
    public static (int Urgency, int Impact) MapSeverity(int severity) => severity switch
    {
        >= 5 => (5, 5),
        4 => (4, 4),
        3 => (3, 3),
        _ => (2, 2),
    };

    public static string StormKey(string clientCode) => $"[STORM:{(clientCode.Length > 0 ? clientCode : "UNMAPPED")}]";

    /// <param name="ev">The event.</param>
    /// <param name="entityId">GLPI entity of the client, or null when the client code is unknown.</param>
    /// <param name="eventTickets">Tickets whose name contains [ZBX:id].</param>
    /// <param name="stormTickets">Tickets whose name contains [STORM:client].</param>
    /// <param name="recentCount">Monitoring tickets opened for the entity inside the storm window.</param>
    public static Decision Decide(ZabbixEvent ev, int? entityId, IReadOnlyList<TicketRef> eventTickets,
        IReadOnlyList<TicketRef> stormTickets, int recentCount, ZabbixDecisionSettings s)
    {
        var mapped = entityId is not null;
        var entity = entityId ?? s.DefaultEntityId;
        var existing = Tickets.FindByKey(eventTickets, ev.Key);
        var openStorm = Tickets.FindByKey(stormTickets, StormKey(ev.ClientCode), openOnly: true);

        switch (ev.Status)
        {
            case ZabbixStatus.Problem:
                if (existing is not null)
                    return new("followup", [Followup(existing.Id, "Repeat notification from Zabbix for the same event.", ev, s)], null, existing.Id);

                if (mapped && recentCount >= s.StormThreshold)
                {
                    if (openStorm is not null)
                        return new("storm_followup", [Followup(openStorm.Id, $"{ev.Key} Additional problem during alert storm.", ev, s)], null, openStorm.Id);
                    var storm = Input(
                        ("name", Text.Truncate($"{StormKey(ev.ClientCode)} Alert storm – {ev.ClientCode}: more than {s.StormThreshold} problems in 5 minutes", 250)),
                        ("entities_id", entity), ("type", 1), ("urgency", 5), ("impact", 4), ("status", TicketStatus.New),
                        ("requesttypes_id", NullIfZero(s.RequestTypeId)), ("_groups_id_assign", NullIfZero(s.NocGroupId)),
                        ("content", Text.HtmlRows($"Alert storm detected for {ev.ClientCode}. Further problems are added to this ticket as follow-ups.",
                            [("First grouped event", ev.Key), .. Details(ev)], Footer)));
                    return new("storm_create", [new("/Ticket", storm)],
                        new("noc", $"WiseSphere STORM {ev.ClientCode}: >{s.StormThreshold} problems in 5 min. Latest: {Text.Truncate($"{ev.HostName} {ev.EventName}", 80)}"));
                }

                var (urgency, impact) = MapSeverity(ev.Severity);
                var title = mapped ? "Monitoring alert raised by Zabbix."
                                   : "Monitoring alert raised by Zabbix for an UNMAPPED host (no client tag). Fix the host tags.";
                var input = Input(
                    ("name", Text.Truncate($"{ev.Key} {ev.HostName}: {ev.EventName}", 250)),
                    ("entities_id", entity), ("type", 1), ("status", TicketStatus.New), ("urgency", urgency), ("impact", impact),
                    ("requesttypes_id", NullIfZero(s.RequestTypeId)), ("_groups_id_assign", NullIfZero(s.NocGroupId)),
                    ("content", Text.HtmlRows(title, Details(ev), Footer)));
                if (ev.GlpiAsset.Length > 0)
                {
                    var parts = ev.GlpiAsset.Split(':');
                    input["items_id"] = new Dictionary<string, int[]> { [parts[0]] = [int.Parse(parts[1])] };
                }
                Notification? page = ev.Severity >= 5
                    ? new("noc", $"WiseSphere P1 {(ev.ClientCode.Length > 0 ? ev.ClientCode : "UNMAPPED")}: {Text.Truncate($"{ev.HostName} – {ev.EventName}", 110)}")
                    : null;
                return new("create", [new("/Ticket", input)], page);

            case ZabbixStatus.Resolved:
                if (existing is not null && TicketStatus.IsOpen(existing.Status))
                {
                    var solution = Input(("itemtype", "Ticket"), ("items_id", existing.Id), ("solutiontypes_id", NullIfZero(s.SolutionTypeId)),
                        ("content", Text.HtmlRows("Problem resolved – Zabbix reported recovery. Engineer to confirm closure.", Details(ev))));
                    return new("solve", [new("/ITILSolution", solution)], null, existing.Id);
                }
                if (existing is null && openStorm is not null)
                    return new("storm_followup", [Followup(openStorm.Id, $"{ev.Key} Recovered.", ev, s)], null, openStorm.Id);
                return Decision.Noop(existing is not null ? "ticket already solved/closed" : "no ticket for event");

            default: // Update: acknowledgement or comment in Zabbix
                if (existing is null) return Decision.Noop("no ticket for event");
                return new("followup", [Followup(existing.Id, "Event updated in Zabbix.", ev, s, [("Updated by", ev.UpdateUser), ("Message", ev.UpdateMessage)])], null, existing.Id);
        }
    }

    private static GlpiWrite Followup(int ticketId, string title, ZabbixEvent ev, ZabbixDecisionSettings s, IEnumerable<(string, string?)>? extra = null) =>
        new("/ITILFollowup", Input(("itemtype", "Ticket"), ("items_id", ticketId), ("is_private", 0),
            ("requesttypes_id", NullIfZero(s.RequestTypeId)), ("content", Text.HtmlRows(title, [.. Details(ev), .. extra ?? []]))));

    private static List<(string, string?)> Details(ZabbixEvent ev) =>
    [
        ("Client", ev.ClientCode.Length > 0 ? ev.ClientCode : "UNMAPPED"),
        ("Site", ev.SiteCode),
        ("Host", ev.HostName != ev.Host ? $"{ev.HostName} ({ev.Host})" : ev.Host),
        ("IP", ev.HostIp),
        ("Problem", ev.EventName),
        ("Severity", ev.SeverityLabel),
        ("Operational data", ev.OpData),
        ("Event time", ev.EventTime),
        ("Zabbix event ID", ev.EventId),
        ("Zabbix link", ev.EventUrl),
    ];

    internal static object? NullIfZero(int v) => v == 0 ? null : v;

    /// <summary>Build a GLPI input object, dropping null values so optional fields are not sent.</summary>
    internal static Dictionary<string, object?> Input(params (string Key, object? Value)[] fields) =>
        fields.Where(f => f.Value is not null).ToDictionary(f => f.Key, f => f.Value);
}

public static class Tickets
{
    /// <summary>Newest ticket whose name starts with <paramref name="key"/> (GLPI LIKE searches can over-match).</summary>
    public static TicketRef? FindByKey(IEnumerable<TicketRef> tickets, string key, bool openOnly = false) =>
        tickets.Where(t => t.Name.StartsWith(key, StringComparison.Ordinal) && (!openOnly || TicketStatus.IsOpen(t.Status)))
               .OrderByDescending(t => t.Id)
               .FirstOrDefault();
}
