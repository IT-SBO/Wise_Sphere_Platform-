using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace WiseSphere.Portal.Services;

/// <summary>Current Zabbix problems for the clients a user may see (host groups CLnnn/...).
/// Uses a read-only Zabbix API token; filtering by the user's client codes happens here.</summary>
public sealed class MonitoringService(HttpClient http, IOptions<ZabbixOptions> options, ILogger<MonitoringService> log)
{
    private readonly ZabbixOptions _o = options.Value;
    private int _id;

    public bool Enabled => _o.ApiUrl.Length > 0 && _o.ApiToken.Length > 0;

    public async Task<IReadOnlyList<Problem>> ProblemsAsync(PortalSession s, CancellationToken ct)
    {
        if (!Enabled) return [];
        // Staff: every client group. Client users: only their own CLnnn/ groups.
        var prefixes = s.IsStaff ? ["CL"] : s.ClientCodes.Select(c => c + "/").ToArray();
        if (prefixes.Length == 0) return [];
        var groups = await CallAsync("hostgroup.get", new { output = new[] { "groupid", "name" }, search = new { name = prefixes }, startSearch = true, searchByAny = true }, ct);
        var groupIds = groups.EnumerateArray().Select(g => g.GetProperty("groupid").GetString()).ToArray();
        if (groupIds.Length == 0) return [];

        var problems = await CallAsync("problem.get", new
        {
            output = new[] { "eventid", "name", "severity", "clock", "acknowledged" },
            groupids = groupIds, recent = false, suppressed = false, sortfield = new[] { "eventid" }, sortorder = "DESC", limit = 300,
        }, ct);
        var ids = problems.EnumerateArray().Select(p => p.GetProperty("eventid").GetString()).ToArray();
        if (ids.Length == 0) return [];
        var events = await CallAsync("event.get", new { eventids = ids, output = new[] { "eventid" }, selectHosts = new[] { "name" }, selectTags = "extend" }, ct);
        var meta = events.EnumerateArray().ToDictionary(
            e => e.GetProperty("eventid").GetString()!,
            e => (Host: e.GetProperty("hosts").EnumerateArray().Select(h => h.GetProperty("name").GetString()).FirstOrDefault() ?? "",
                  Client: e.GetProperty("tags").EnumerateArray().Where(t => t.GetProperty("tag").GetString() == "client")
                           .Select(t => (t.GetProperty("value").GetString() ?? "").ToUpperInvariant()).FirstOrDefault() ?? ""));

        var allowed = s.ClientCodes.ToHashSet(StringComparer.Ordinal);
        return problems.EnumerateArray().Select(p =>
        {
            var id = p.GetProperty("eventid").GetString()!;
            var m = meta.GetValueOrDefault(id);
            return new Problem(id, p.GetProperty("name").GetString() ?? "", int.Parse(p.GetProperty("severity").GetString()!), m.Host, m.Client,
                DateTimeOffset.FromUnixTimeSeconds(long.Parse(p.GetProperty("clock").GetString()!)), p.GetProperty("acknowledged").GetString() == "1");
        })
        // defence in depth: a client never sees an event tagged for another client
        .Where(p => s.IsStaff || allowed.Contains(p.Client))
        .ToList();
    }

    /// <summary>Problems raised for a client's host groups in [from, to) and their total duration in hours (clipped to the period).</summary>
    public async Task<(int Count, double Hours)> ProblemTotalsAsync(string client, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        if (!Enabled) return (0, 0);
        var groups = await CallAsync("hostgroup.get", new { output = new[] { "groupid" }, search = new { name = new[] { client + "/" } }, startSearch = true }, ct);
        var groupIds = groups.EnumerateArray().Select(g => g.GetProperty("groupid").GetString()).ToArray();
        if (groupIds.Length == 0) return (0, 0);
        var events = await CallAsync("event.get", new
        {
            output = new[] { "eventid", "clock", "r_eventid" }, groupids = groupIds, source = 0, @object = 0, value = 1,
            time_from = from.ToUnixTimeSeconds(), time_till = to.ToUnixTimeSeconds() - 1,
        }, ct);
        var list = events.EnumerateArray().ToList();
        var recoveryIds = list.Select(e => e.GetProperty("r_eventid").GetString()).Where(r => r is not null and not "0").ToArray();
        var recoveries = recoveryIds.Length == 0 ? new Dictionary<string, long>() :
            (await CallAsync("event.get", new { output = new[] { "eventid", "clock" }, eventids = recoveryIds }, ct)).EnumerateArray()
            .ToDictionary(e => e.GetProperty("eventid").GetString()!, e => long.Parse(e.GetProperty("clock").GetString()!));
        var end = Math.Min(to.ToUnixTimeSeconds(), DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        double seconds = 0;
        foreach (var e in list)
        {
            var start = long.Parse(e.GetProperty("clock").GetString()!);
            var stop = recoveries.TryGetValue(e.GetProperty("r_eventid").GetString() ?? "", out var r) ? Math.Min(r, end) : end;
            seconds += Math.Max(0, stop - start);
        }
        return (list.Count, seconds / 3600);
    }

    private async Task<JsonElement> CallAsync(string method, object @params, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new { jsonrpc = "2.0", method, @params, id = Interlocked.Increment(ref _id) });
        using var req = new HttpRequestMessage(HttpMethod.Post, _o.ApiUrl) { Content = new StringContent(body, Encoding.UTF8, "application/json-rpc") };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _o.ApiToken);
        using var res = await http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        if (doc.RootElement.TryGetProperty("error", out var err))
        {
            log.LogWarning("Zabbix {Method} failed: {Error}", method, err.ToString());
            throw new InvalidOperationException($"Zabbix {method}: {err}");
        }
        return doc.RootElement.GetProperty("result").Clone();
    }
}
