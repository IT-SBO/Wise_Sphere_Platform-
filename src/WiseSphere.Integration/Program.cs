using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using WiseSphere.Core;
using WiseSphere.Glpi;
using WiseSphere.Integration;
using WiseSphere.Integration.Services;
using WiseSphere.Integration.Workers;

// WiseSphere integration service - replaces n8n (PRD INT-01..INT-07).
//   POST /webhook/zbx-event      Zabbix media type  (ws.event.v1)
//   POST /webhook/wazuh-alert    Wazuh custom-n8n   (ws.secevent.v1)
//   POST /jobs/{sla-watch|csat-review}/run   manual run of a scheduled job (same secret header)
//   GET  /healthz  /readyz  /metrics
var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables("WS__");
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(o => { o.IncludeScopes = false; o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ "; o.UseUtcTimestamp = true; });

builder.Services.Configure<IntegrationOptions>(builder.Configuration.GetSection(IntegrationOptions.Section));
builder.Services.Configure<GlpiOptions>(builder.Configuration.GetSection(GlpiOptions.Section));
builder.Services.Configure<SmsOptions>(builder.Configuration.GetSection(SmsOptions.Section));
builder.Services.AddSingleton(sp => PolicyLoader.Load(sp.GetRequiredService<IOptions<IntegrationOptions>>().Value.ConfigDir));
// Long-lived singletons (GLPI session pool) with connection recycling so DNS changes are picked up
static HttpClient NewHttp(int timeoutSeconds) =>
    new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
builder.Services.AddSingleton(sp => ActivatorUtilities.CreateInstance<GlpiClient>(sp, NewHttp(30)));
builder.Services.AddSingleton(sp => ActivatorUtilities.CreateInstance<SmsSender>(sp, NewHttp(10)));
builder.Services.AddSingleton<Metrics>();
builder.Services.AddSingleton<KeyedLock>();
builder.Services.AddSingleton<GlpiCatalog>();
builder.Services.AddSingleton<StateStore>();
builder.Services.AddSingleton<FailureNotifier>();
builder.Services.AddSingleton<IntakeService>();
builder.Services.AddSingleton<SlaWatchJob>();
builder.Services.AddSingleton<CsatReviewJob>();
builder.Services.AddHostedService<SchedulerWorker>();

var app = builder.Build();
var opts = app.Services.GetRequiredService<IOptions<IntegrationOptions>>().Value;
if (opts.WebhookSecret.Length < 16) throw new InvalidOperationException("Integration:WebhookSecret must be set (>= 16 characters)");
var secret = Encoding.UTF8.GetBytes(opts.WebhookSecret);
_ = app.Services.GetRequiredService<PlatformPolicy>(); // fail fast on bad tenants.yaml / sla.yaml

// INT-05: every webhook and job call needs the shared secret (constant-time comparison)
bool Authorized(HttpRequest r) =>
    r.Headers.TryGetValue("X-WS-Secret", out var v) && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(v.ToString()), secret);

app.MapPost("/webhook/zbx-event", async (HttpRequest req, IntakeService intake, FailureNotifier failures, Metrics metrics, CancellationToken ct) =>
{
    if (!Authorized(req)) { metrics.Inc("ws_webhook_rejected_total", "auth"); return Results.Json(new { ok = false, error = "forbidden" }, statusCode: 403); }
    JsonElement body;
    try { body = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body, cancellationToken: ct); }
    catch (JsonException) { return Results.BadRequest(new { ok = false, errors = new[] { "invalid JSON" } }); }
    var parsed = ZabbixEventParser.Parse(body);
    if (!parsed.IsValid) { metrics.Inc("ws_webhook_rejected_total", "invalid"); return Results.BadRequest(new { ok = false, errors = parsed.Errors }); }
    try { return Results.Ok(Present(await intake.HandleZabbixAsync(parsed.Value!, ct))); }
    catch (Exception e) when (e is not OperationCanceledException)
    {
        failures.Report("zabbix-intake", e);
        return Results.Json(new { ok = false, error = e.Message }, statusCode: 502); // Zabbix retries (3 attempts)
    }
});

app.MapPost("/webhook/wazuh-alert", async (HttpRequest req, IntakeService intake, FailureNotifier failures, Metrics metrics, CancellationToken ct) =>
{
    if (!Authorized(req)) { metrics.Inc("ws_webhook_rejected_total", "auth"); return Results.Json(new { ok = false, error = "forbidden" }, statusCode: 403); }
    JsonElement body;
    try { body = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body, cancellationToken: ct); }
    catch (JsonException) { return Results.BadRequest(new { ok = false, errors = new[] { "invalid JSON" } }); }
    var parsed = SecurityAlertParser.Parse(body);
    if (!parsed.IsValid) { metrics.Inc("ws_webhook_rejected_total", "invalid"); return Results.BadRequest(new { ok = false, errors = parsed.Errors }); }
    try { return Results.Ok(Present(await intake.HandleSecurityAsync(parsed.Value!, ct))); }
    catch (Exception e) when (e is not OperationCanceledException)
    {
        failures.Report("wazuh-intake", e);
        return Results.Json(new { ok = false, error = e.Message }, statusCode: 502);
    }
});

app.MapPost("/jobs/sla-watch/run", async (HttpRequest req, SlaWatchJob job, CancellationToken ct) =>
    Authorized(req) ? Results.Ok(new { ok = true, sms = await job.RunAsync(ct) }) : Results.StatusCode(403));
app.MapPost("/jobs/csat-review/run", async (HttpRequest req, CsatReviewJob job, CancellationToken ct) =>
    Authorized(req) ? Results.Ok(new { ok = true, tasks = await job.RunAsync(ct) }) : Results.StatusCode(403));

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
app.MapGet("/readyz", async (GlpiClient glpi, CancellationToken ct) =>
    await glpi.PingAsync(ct) ? Results.Ok(new { status = "ready" }) : Results.Json(new { status = "glpi unreachable" }, statusCode: 503));
app.MapGet("/metrics", (Metrics m) => Results.Text(m.Render(), "text/plain; version=0.0.4"));

app.Run();

static object Present(IntakeResult r) => new
{
    ok = r.Ok, action = r.Action, client_code = r.ClientCode, entity_id = r.EntityId, mapped = r.Mapped,
    ticket_id = r.TicketId, paged = r.Paged, reason = r.Reason,
};

/// <summary>Exposed for integration tests (WebApplicationFactory).</summary>
public partial class Program;
