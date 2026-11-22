using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using WiseSphere.Glpi;

namespace WiseSphere.Portal.Services;

/// <summary>Ticket queries for the portal. Every call runs with the user's own GLPI session,
/// so GLPI enforces what each user may see (client isolation, TC-10).</summary>
public sealed class TicketService(GlpiClient glpi, IOptions<PortalOptions> options)
{
    private readonly PortalOptions _o = options.Value;
    private readonly TimeZoneInfo _tz = TimeZoneInfo.FindSystemTimeZoneById(options.Value.GlpiTimeZone);

    // GLPI search option ids for Ticket
    private const string FName = "1", FId = "2", FPriority = "3", FRequestType = "9", FStatus = "12", FOpened = "15",
        FDue = "18", FUpdated = "19", FEntity = "80";
    private static readonly string[] Display = [FName, FId, FPriority, FRequestType, FStatus, FOpened, FDue, FUpdated, FEntity];

    public async Task<IReadOnlyList<TicketRow>> SearchAsync(PortalSession s, string scope, int page, int pageSize, CancellationToken ct)
    {
        var q = new List<(string, string)>();
        if (scope is "open" or "solved")
            q.AddRange([("criteria[0][field]", FStatus), ("criteria[0][searchtype]", "equals"), ("criteria[0][value]", scope == "open" ? "notold" : "old")]);
        for (var i = 0; i < Display.Length; i++) q.Add(($"forcedisplay[{i}]", Display[i]));
        q.AddRange([("sort", FUpdated), ("order", "DESC"), ("range", $"{page * pageSize}-{page * pageSize + pageSize - 1}")]);
        var res = await glpi.UserRequestAsync(s.GlpiToken, HttpMethod.Get, "/search/Ticket" + GlpiClient.Query([.. q]), null, ct);
        if (res is not { ValueKind: JsonValueKind.Object } o || !o.TryGetProperty("data", out var data)) return [];
        return data.EnumerateArray().Select(Row).ToList();
    }

    public async Task<Dashboard> DashboardAsync(PortalSession s, CancellationToken ct)
    {
        var open = await SearchAsync(s, "open", 0, 1000, ct);
        var recent = await SearchAsync(s, "all", 0, 8, ct);
        var now = DateTimeOffset.UtcNow;
        bool Breached(TicketRow t) => t.DueAt is { } d && d < now;
        bool AtRisk(TicketRow t) => t.DueAt is { } d && d >= now && d < now.AddMinutes(_o.AtRiskMinutes);

        var clients = s.Entities.Where(e => e.ClientCode.Length > 0).GroupBy(e => e.ClientCode).Select(g =>
        {
            var mine = open.Where(t => t.Client == g.Key).ToList();
            var p1 = mine.Count(t => t.Priority >= 5);
            var p2 = mine.Count(t => t.Priority == 4);
            var breached = mine.Count(Breached);
            var status = p1 > 0 || breached > 0 ? "red" : p2 > 0 || mine.Any(AtRisk) ? "amber" : "green";
            return new ClientHealth(g.Key, g.First().Name.Split('>').Last().Trim(), p1, p2, mine.Count, breached, mine.Count(AtRisk), status);
        }).OrderBy(c => c.Status switch { "red" => 0, "amber" => 1, _ => 2 }).ThenBy(c => c.Client).ToList();

        return new Dashboard(open.Count, open.Count(t => t.Priority >= 5), open.Count(t => t.Priority == 4),
            open.Count(AtRisk), open.Count(Breached), open.Count(t => t.Title.StartsWith("[STORM:", StringComparison.Ordinal)),
            clients, open.Where(t => t.DueAt is not null).OrderBy(t => t.DueAt).Take(8).ToList(), recent);
    }

    public async Task<TicketDetail?> GetAsync(PortalSession s, int id, CancellationToken ct)
    {
        JsonElement? t;
        try { t = await glpi.UserRequestAsync(s.GlpiToken, HttpMethod.Get, $"/Ticket/{id}" + GlpiClient.Query(("expand_dropdowns", "true")), null, ct); }
        catch (GlpiException e) when (e.Status is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) { return null; }
        if (t is not { ValueKind: JsonValueKind.Object } ticket) return null;

        var entity = Str(ticket, "entities_id");
        var row = new TicketRow(id, Str(ticket, "name"), Int(ticket, "status"), Int(ticket, "priority"), SessionStore.ClientCode(entity), entity,
            Date(Str(ticket, "date")), Date(Str(ticket, "time_to_resolve")), Date(Str(ticket, "date_mod")), Str(ticket, "requesttypes_id"));

        var timeline = new List<TimelineItem>();
        foreach (var (sub, kind) in new[] { ("ITILFollowup", "followup"), ("TicketTask", "task"), ("ITILSolution", "solution") })
        {
            JsonElement? items;
            try { items = await glpi.UserRequestAsync(s.GlpiToken, HttpMethod.Get, $"/Ticket/{id}/{sub}" + GlpiClient.Query(("expand_dropdowns", "true"), ("range", "0-200")), null, ct); }
            catch (GlpiException) { continue; } // e.g. a client profile without task rights
            if (items is not { ValueKind: JsonValueKind.Array } arr) continue;
            timeline.AddRange(arr.EnumerateArray().Select(i => new TimelineItem(kind, Date(Str(i, "date_creation").Length > 0 ? Str(i, "date_creation") : Str(i, "date")),
                Str(i, "users_id"), Html(Str(i, "content")), Int(i, "is_private") == 1)));
        }
        return new TicketDetail(row, Html(Str(ticket, "content")), timeline.OrderBy(x => x.At).ToList(),
            $"{_o.GlpiPublicUrl.TrimEnd('/')}/front/ticket.form.php?id={id}");
    }

    public async Task<int> AddFollowupAsync(PortalSession s, int ticketId, string text, CancellationToken ct)
    {
        var res = await glpi.UserRequestAsync(s.GlpiToken, HttpMethod.Post, "/ITILFollowup",
            new { input = new { itemtype = "Ticket", items_id = ticketId, content = TextToHtml(text) } }, ct);
        return res!.Value.GetProperty("id").GetInt32();
    }

    public async Task<int> CreateAsync(PortalSession s, int entityId, string title, string text, int urgency, CancellationToken ct)
    {
        var res = await glpi.UserRequestAsync(s.GlpiToken, HttpMethod.Post, "/Ticket",
            new { input = new { name = title, content = TextToHtml(text), entities_id = entityId, urgency, type = 1 } }, ct);
        return res!.Value.GetProperty("id").GetInt32();
    }

    // ------------------------------------------------------------------ mapping helpers

    private TicketRow Row(JsonElement r)
    {
        var entity = StripTags(Str(r, FEntity));
        return new TicketRow(Int(r, FId), StripTags(Str(r, FName)), Int(r, FStatus), Int(r, FPriority), SessionStore.ClientCode(entity), entity,
            Date(Str(r, FOpened)), Date(Str(r, FDue)), Date(Str(r, FUpdated)), StripTags(Str(r, FRequestType)));
    }

    private DateTimeOffset? Date(string s)
    {
        if (!DateTime.TryParseExact(s, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)) return null;
        return new DateTimeOffset(local, _tz.GetUtcOffset(local));
    }

    private static string Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) ? v.ValueKind switch { JsonValueKind.String => v.GetString() ?? "", JsonValueKind.Number => v.GetRawText(), _ => "" } : "";

    private static int Int(JsonElement o, string name) => int.TryParse(Str(o, name), out var n) ? n : 0;
    private static string StripTags(string s) => System.Text.RegularExpressions.Regex.Replace(s, "<[^>]*>", "").Trim();
    /// <summary>GLPI may return rich text HTML-encoded; the browser sanitises it again before display.</summary>
    private static string Html(string s) => s.Contains("&lt;", StringComparison.Ordinal) ? WebUtility.HtmlDecode(s) : s;
    private static string TextToHtml(string text) =>
        string.Concat(text.Split('\n').Select(l => $"<p>{WebUtility.HtmlEncode(l.TrimEnd('\r'))}</p>"));
}
