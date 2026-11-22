using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WiseSphere.Core;

namespace WiseSphere.Glpi;

public sealed class GlpiOptions
{
    public const string Section = "Glpi";
    /// <summary>Legacy REST API base, e.g. http://glpi/apirest.php (internal network only).</summary>
    public string ApiUrl { get; set; } = "http://glpi/apirest.php";
    public string AppToken { get; set; } = "";
    public string User { get; set; } = "svc-integration";
    public string Password { get; set; } = "";
    /// <summary>Parallel GLPI sessions. PHP locks a session per request, so parallel work needs several sessions.</summary>
    public int SessionPoolSize { get; set; } = 4;
    public int TimeoutSeconds { get; set; } = 20;
}

public sealed class GlpiException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>
/// Typed client for the GLPI 11 legacy REST API (apirest.php) used by the integration service.
/// Authenticates the service account with login + app token (GLPI 11 stores user API tokens encrypted),
/// scopes every session to the root entity recursively, and keeps a small pool of sessions.
/// </summary>
public sealed class GlpiClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly GlpiOptions _o;
    private readonly ILogger<GlpiClient> _log;
    private readonly Channel<string> _idle;
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentDictionary<string, (int? Id, DateTimeOffset Expires)> _entityCache = new();

    public GlpiClient(HttpClient http, IOptions<GlpiOptions> options, ILogger<GlpiClient> log)
    {
        _http = http;
        _o = options.Value;
        _log = log;
        _idle = Channel.CreateUnbounded<string>();
        _slots = new SemaphoreSlim(Math.Max(1, _o.SessionPoolSize));
    }

    // ------------------------------------------------------------------ sessions

    private async Task<string> OpenSessionAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_o.ApiUrl}/initSession");
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_o.User}:{_o.Password}")));
        AddAppToken(req);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_o.TimeoutSeconds));
        using var res = await _http.SendAsync(req, cts.Token);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new GlpiException($"GLPI initSession failed: HTTP {(int)res.StatusCode} {Trim(body)}", res.StatusCode);
        var token = JsonDocument.Parse(body).RootElement.GetProperty("session_token").GetString()!;
        // all client entities, recursively (tickets are searched across clients)
        await SendRawAsync(token, HttpMethod.Post, "/changeActiveEntities", new { entities_id = 0, is_recursive = true }, ct);
        _log.LogInformation("GLPI session opened");
        return token;
    }

    /// <summary>Run <paramref name="work"/> with a pooled session; on an invalid session, reopen once and retry.</summary>
    private async Task<T> WithSessionAsync<T>(Func<string, Task<T>> work, CancellationToken ct)
    {
        await _slots.WaitAsync(ct);
        string? token = null;
        try
        {
            token = _idle.Reader.TryRead(out var t) ? t : await OpenSessionAsync(ct);
            try
            {
                return await work(token);
            }
            catch (GlpiException e) when (e.Status == HttpStatusCode.Unauthorized)
            {
                _log.LogWarning("GLPI session expired, reopening");
                token = await OpenSessionAsync(ct);
                return await work(token);
            }
        }
        catch
        {
            token = null; // do not return a possibly broken session to the pool
            throw;
        }
        finally
        {
            if (token is not null) _idle.Writer.TryWrite(token);
            _slots.Release();
        }
    }

    private void AddAppToken(HttpRequestMessage req)
    {
        if (_o.AppToken.Length > 0) req.Headers.Add("App-Token", _o.AppToken);
    }

    private async Task<JsonElement?> SendRawAsync(string session, HttpMethod method, string pathAndQuery, object? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, _o.ApiUrl + pathAndQuery);
        req.Headers.Add("Session-Token", session);
        AddAppToken(req);
        if (body is not null) req.Content = JsonContent.Create(body, options: JsonOpts);
        using var res = await _http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        // GLPI answers 400 ERROR_RANGE_EXCEED_TOTAL for an empty result page
        if (res.StatusCode == HttpStatusCode.BadRequest && text.Contains("ERROR_RANGE_EXCEED_TOTAL", StringComparison.Ordinal)) return null;
        if (!res.IsSuccessStatusCode)
            throw new GlpiException($"GLPI {method} {pathAndQuery.Split('?')[0]} failed: HTTP {(int)res.StatusCode} {Trim(text)}", res.StatusCode);
        return text.Length == 0 ? null : JsonDocument.Parse(text).RootElement.Clone();
    }

    private Task<JsonElement?> SendAsync(HttpMethod method, string pathAndQuery, object? body, CancellationToken ct) =>
        WithSessionAsync(s => SendRawAsync(s, method, pathAndQuery, body, ct), ct);

    // ------------------------------------------------------------------ queries

    private static string Q(params (string Key, string Value)[] p) =>
        "?" + string.Join("&", p.Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value)}"));

    /// <summary>Client entity id by tag (CLnnn). Cached 5 min (1 min when unknown).</summary>
    public async Task<int?> FindEntityIdByTagAsync(string clientCode, CancellationToken ct)
    {
        if (clientCode.Length == 0) return null;
        if (_entityCache.TryGetValue(clientCode, out var c) && c.Expires > DateTimeOffset.UtcNow) return c.Id;
        var rows = await SendAsync(HttpMethod.Get, "/Entity" + Q(("searchText[tag]", $"^{clientCode}$"), ("range", "0-1")), null, ct);
        int? id = rows is { ValueKind: JsonValueKind.Array } a && a.GetArrayLength() > 0 ? a[0].GetProperty("id").GetInt32() : null;
        _entityCache[clientCode] = (id, DateTimeOffset.UtcNow.AddMinutes(id is null ? 1 : 5));
        return id;
    }

    /// <summary>Tickets whose name contains <paramref name="text"/> (newest first).</summary>
    public async Task<IReadOnlyList<TicketRef>> FindTicketsByNameAsync(string text, CancellationToken ct)
    {
        var rows = await SendAsync(HttpMethod.Get, "/Ticket" + Q(("searchText[name]", text), ("range", "0-20"), ("order", "DESC")), null, ct);
        if (rows is not { ValueKind: JsonValueKind.Array } arr) return [];
        return arr.EnumerateArray()
            .Select(t => new TicketRef(t.GetProperty("id").GetInt32(), t.GetProperty("name").GetString() ?? "", t.GetProperty("status").GetInt32()))
            .ToList();
    }

    /// <summary>Number of tickets of an entity whose name contains <paramref name="nameContains"/>, opened in the last N minutes.
    /// The time window is evaluated by GLPI itself (no clock or time-zone mismatch).</summary>
    public async Task<int> CountRecentTicketsAsync(int entityId, string nameContains, int minutes, CancellationToken ct)
    {
        var res = await SendAsync(HttpMethod.Get, "/search/Ticket" + Q(
            ("criteria[0][field]", "1"), ("criteria[0][searchtype]", "contains"), ("criteria[0][value]", nameContains),
            ("criteria[1][link]", "AND"), ("criteria[1][field]", "15"), ("criteria[1][searchtype]", "morethan"), ("criteria[1][value]", $"-{minutes}MINUTE"),
            ("criteria[2][link]", "AND"), ("criteria[2][field]", "80"), ("criteria[2][searchtype]", "equals"), ("criteria[2][value]", entityId.ToString()),
            ("forcedisplay[0]", "2"), ("range", "0-0")), null, ct);
        return res is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty("totalcount", out var n) ? n.GetInt32() : 0;
    }

    /// <summary>POST /{itemtype} with {"input": ...}; returns the new id.</summary>
    public async Task<int> CreateAsync(GlpiWrite write, CancellationToken ct)
    {
        var res = await SendAsync(HttpMethod.Post, write.Path, new { input = write.Input }, ct);
        if (res is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty("id", out var id)) return id.GetInt32();
        throw new GlpiException($"GLPI POST {write.Path} returned no id");
    }

    /// <summary>Open tickets whose time-to-own (status New only) or time-to-resolve is at risk / breached.</summary>
    public async Task<IReadOnlyList<SlaCandidate>> SearchSlaAsync(SlaKind kind, SlaStage stage, int atRiskMinutes, CancellationToken ct)
    {
        var field = kind == SlaKind.TimeToOwn ? "155" : "18";
        var status = kind == SlaKind.TimeToOwn ? "1" : "notold";
        var crit = new List<(string, string)>
        {
            ("criteria[0][field]", "12"), ("criteria[0][searchtype]", "equals"), ("criteria[0][value]", status),
            ("criteria[1][link]", "AND"), ("criteria[1][field]", field), ("criteria[1][searchtype]", "lessthan"),
            ("criteria[1][value]", stage == SlaStage.Breached ? "NOW" : $"{atRiskMinutes}MINUTE"),
        };
        if (stage == SlaStage.AtRisk)
            crit.AddRange([("criteria[2][link]", "AND"), ("criteria[2][field]", field), ("criteria[2][searchtype]", "morethan"), ("criteria[2][value]", "NOW")]);
        crit.AddRange([("forcedisplay[0]", "1"), ("forcedisplay[1]", "2"), ("forcedisplay[2]", "3"), ("forcedisplay[3]", "80"), ("range", "0-200")]);
        var res = await SendAsync(HttpMethod.Get, "/search/Ticket" + Q([.. crit]), null, ct);
        if (res is not { ValueKind: JsonValueKind.Object } o || !o.TryGetProperty("data", out var data)) return [];
        return data.EnumerateArray().Select(r => new SlaCandidate(
            int.Parse(Json.Str(r, "2") ?? "0"),
            StripHtml(Json.Str(r, "1")),
            int.TryParse(Json.Str(r, "3"), out var p) ? p : 3,
            StripHtml(Json.Str(r, "80")),
            kind, stage)).ToList();
    }

    /// <summary>Most recent satisfaction survey rows.</summary>
    public async Task<IReadOnlyList<SurveyAnswer>> GetRecentSurveysAsync(CancellationToken ct)
    {
        var rows = await SendAsync(HttpMethod.Get, "/TicketSatisfaction" + Q(("range", "0-500"), ("order", "DESC")), null, ct);
        if (rows is not { ValueKind: JsonValueKind.Array } arr) return [];
        return arr.EnumerateArray().Select(r => new SurveyAnswer(
            r.GetProperty("id").GetInt32(), r.GetProperty("tickets_id").GetInt32(),
            int.TryParse(Json.Str(r, "satisfaction"), out var s) ? s : null, Json.Str(r, "comment"))).ToList();
    }

    /// <summary>Id of an item by exact name (catalog lookups at start-up: request types, groups, solution types).</summary>
    public async Task<int?> FindIdByNameAsync(string itemtype, string name, CancellationToken ct)
    {
        var rows = await SendAsync(HttpMethod.Get, $"/{itemtype}" + Q(("searchText[name]", $"^{name}$"), ("range", "0-5")), null, ct);
        if (rows is not { ValueKind: JsonValueKind.Array } arr) return null;
        return arr.EnumerateArray().Where(r => r.GetProperty("name").GetString() == name).Select(r => (int?)r.GetProperty("id").GetInt32()).FirstOrDefault();
    }

    public async Task<bool> PingAsync(CancellationToken ct)
    {
        try { await SendAsync(HttpMethod.Get, "/getActiveEntities", null, ct); return true; }
        catch (Exception e) when (e is GlpiException or HttpRequestException or TaskCanceledException) { _log.LogWarning(e, "GLPI ping failed"); return false; }
    }

    private static string StripHtml(string? s) => System.Text.RegularExpressions.Regex.Replace(s ?? "", "<[^>]*>", "").Trim();
    private static string Trim(string s) => s.Length > 300 ? s[..300] : s;

    public async ValueTask DisposeAsync()
    {
        _idle.Writer.TryComplete();
        while (_idle.Reader.TryRead(out var token))
        {
            try { await SendRawAsync(token, HttpMethod.Get, "/killSession", null, CancellationToken.None); }
            catch (Exception e) when (e is GlpiException or HttpRequestException or TaskCanceledException) { /* best effort on shutdown */ }
        }
        _slots.Dispose();
    }
}
