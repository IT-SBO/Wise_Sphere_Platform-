using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using WiseSphere.Glpi;

namespace WiseSphere.Portal.Services;

public sealed record CountItem(string Label, int Count);

/// <summary>RPT-01 monthly client report. Figures come from GLPI (user's own session) and Zabbix (read-only).</summary>
public sealed record MonthlyReport(
    string Client, string ClientName, string Month, DateTimeOffset GeneratedAt,
    int TicketsOpened, int TicketsSolved, int StillOpen,
    IReadOnlyList<CountItem> ByPriority, IReadOnlyList<CountItem> BySource,
    double? SlaMetPercent, int SlaMeasured, double? AvgResolveHours,
    double? Csat, int CsatAnswers, int AlertStorms, int SecurityIncidents,
    IReadOnlyList<CountItem> TopAssets, int MonitoringProblems, double MonitoringProblemHours,
    IReadOnlyList<TicketRow> MajorIncidents);

public sealed partial class ReportService(GlpiClient glpi, MonitoringService monitoring, IOptions<PortalOptions> options)
{
    private readonly TimeZoneInfo _tz = TimeZoneInfo.FindSystemTimeZoneById(options.Value.GlpiTimeZone);

    [GeneratedRegex(@"^\[ZBX:\d+\]\s+(?<host>[^:]+):")] private static partial Regex ZabbixHost();

    /// <returns>null when the user may not see this client.</returns>
    public async Task<MonthlyReport?> MonthlyAsync(PortalSession s, string client, string month, CancellationToken ct)
    {
        var entity = s.Entities.FirstOrDefault(e => e.ClientCode == client);
        if (entity is null) return null;
        if (!DateOnly.TryParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var first))
            throw new ArgumentException("month must be YYYY-MM");
        var start = first.ToDateTime(TimeOnly.MinValue);
        var end = first.AddMonths(1).ToDateTime(TimeOnly.MinValue);

        // Tickets opened in the month for the client entity (dates in GLPI's time zone)
        var q = new List<(string, string)>
        {
            ("criteria[0][field]", "80"), ("criteria[0][searchtype]", "equals"), ("criteria[0][value]", entity.Id.ToString(CultureInfo.InvariantCulture)),
            ("criteria[1][link]", "AND"), ("criteria[1][field]", "15"), ("criteria[1][searchtype]", "morethan"), ("criteria[1][value]", start.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
            ("criteria[2][link]", "AND"), ("criteria[2][field]", "15"), ("criteria[2][searchtype]", "lessthan"), ("criteria[2][value]", end.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
        };
        string[] fields = ["1", "2", "3", "9", "12", "15", "17", "18", "80"]; // name id priority source status opened solved ttr entity
        for (var i = 0; i < fields.Length; i++) q.Add(($"forcedisplay[{i}]", fields[i]));
        q.Add(("range", "0-4999"));
        var res = await glpi.UserRequestAsync(s.GlpiToken, HttpMethod.Get, "/search/Ticket" + GlpiClient.Query([.. q]), null, ct);
        var rows = res is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty("data", out var data) ? data.EnumerateArray().ToList() : [];

        var tickets = rows.Select(r => new
        {
            Id = Int(r, "2"), Name = Strip(Str(r, "1")), Priority = Int(r, "3"), Source = Strip(Str(r, "9")), Status = Int(r, "12"),
            Opened = Date(Str(r, "15")), Solved = Date(Str(r, "17")), Due = Date(Str(r, "18")), Entity = Strip(Str(r, "80")),
        }).ToList();

        var solved = tickets.Where(t => t.Status >= 5).ToList();
        var measured = solved.Where(t => t.Due is not null && t.Solved is not null).ToList();
        var resolveHours = solved.Where(t => t.Opened is not null && t.Solved is not null).Select(t => (t.Solved!.Value - t.Opened!.Value).TotalHours).ToList();
        static string P(int p) => p >= 5 ? "P1" : p == 4 ? "P2" : p == 3 ? "P3" : "P4";

        // CSAT for these tickets (not every profile may read surveys - then it is left empty)
        double? csat = null; var csatAnswers = 0;
        try
        {
            var ids = tickets.Select(t => t.Id).ToHashSet();
            var sat = await glpi.UserRequestAsync(s.GlpiToken, HttpMethod.Get, "/TicketSatisfaction" + GlpiClient.Query(("range", "0-4999")), null, ct);
            var scores = sat is { ValueKind: JsonValueKind.Array } arr
                ? arr.EnumerateArray().Where(x => ids.Contains(Int(x, "tickets_id")) && int.TryParse(Str(x, "satisfaction"), out _)).Select(x => Int(x, "satisfaction")).ToList()
                : [];
            csatAnswers = scores.Count;
            if (scores.Count > 0) csat = Math.Round(scores.Average(), 2);
        }
        catch (GlpiException) { /* no survey rights for this profile */ }

        var (problems, problemHours) = await monitoring.ProblemTotalsAsync(client, new DateTimeOffset(start, _tz.GetUtcOffset(start)), new DateTimeOffset(end, _tz.GetUtcOffset(end)), ct);

        return new MonthlyReport(
            client, entity.Name.Split('>').Last().Trim(), month, DateTimeOffset.UtcNow,
            tickets.Count, solved.Count, tickets.Count - solved.Count,
            new[] { 5, 4, 3, 2 }.Select(p => new CountItem(P(p), tickets.Count(t => P(t.Priority) == P(p)))).ToList(),
            tickets.GroupBy(t => t.Source.Length > 0 ? t.Source : "–").Select(g => new CountItem(g.Key, g.Count())).OrderByDescending(c => c.Count).ToList(),
            measured.Count > 0 ? Math.Round(100.0 * measured.Count(t => t.Solved <= t.Due) / measured.Count, 1) : null, measured.Count,
            resolveHours.Count > 0 ? Math.Round(resolveHours.Average(), 1) : null,
            csat, csatAnswers,
            tickets.Count(t => t.Name.StartsWith("[STORM:", StringComparison.Ordinal)),
            tickets.Count(t => t.Name.StartsWith("[WZH:", StringComparison.Ordinal)),
            tickets.Select(t => ZabbixHost().Match(t.Name)).Where(m => m.Success).GroupBy(m => m.Groups["host"].Value.Trim())
                .Select(g => new CountItem(g.Key, g.Count())).OrderByDescending(c => c.Count).Take(5).ToList(),
            problems, Math.Round(problemHours, 1),
            tickets.Where(t => t.Priority >= 5).OrderBy(t => t.Opened).Take(20)
                .Select(t => new TicketRow(t.Id, t.Name, t.Status, t.Priority, client, t.Entity, t.Opened, t.Due, t.Solved, t.Source)).ToList());
    }

    private DateTimeOffset? Date(string v) =>
        DateTime.TryParseExact(v, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? new DateTimeOffset(d, _tz.GetUtcOffset(d)) : null;
    private static string Str(JsonElement o, string n) =>
        o.TryGetProperty(n, out var v) ? v.ValueKind switch { JsonValueKind.String => v.GetString() ?? "", JsonValueKind.Number => v.GetRawText(), _ => "" } : "";
    private static int Int(JsonElement o, string n) => int.TryParse(Str(o, n), out var i) ? i : 0;
    private static string Strip(string v) => Regex.Replace(v, "<[^>]*>", "").Trim();
}
