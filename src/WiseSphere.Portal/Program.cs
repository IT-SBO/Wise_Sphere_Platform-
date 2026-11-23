using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using WiseSphere.Glpi;
using WiseSphere.Portal.Services;

// WiseSphere portal backend (ASP.NET Core) + Angular front-end (wwwroot).
// Users sign in with their GLPI account; all ticket data is read with that user's GLPI session,
// so GLPI's entity rights decide what each user sees (client isolation, TC-10).
var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables("WS__");
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(o => { o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ "; o.UseUtcTimestamp = true; });

builder.Services.Configure<PortalOptions>(builder.Configuration.GetSection(PortalOptions.Section));
builder.Services.Configure<GlpiOptions>(builder.Configuration.GetSection(GlpiOptions.Section));
builder.Services.Configure<ZabbixOptions>(builder.Configuration.GetSection(ZabbixOptions.Section));
builder.Services.Configure<IntegrationOptions>(builder.Configuration.GetSection(IntegrationOptions.Section));
static HttpClient NewHttp() => new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = TimeSpan.FromSeconds(150) };
builder.Services.AddSingleton(sp => ActivatorUtilities.CreateInstance<GlpiClient>(sp, NewHttp()));
builder.Services.AddSingleton(sp => ActivatorUtilities.CreateInstance<MonitoringService>(sp, NewHttp()));
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<TicketService>();
builder.Services.AddSingleton<ReportService>();
builder.Services.AddSingleton(sp => ActivatorUtilities.CreateInstance<ContainmentClient>(sp, NewHttp()));
builder.Services.AddProblemDetails();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    // brute-force protection on the login form: 10 attempts per minute per client IP
    o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "?",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();
var portal = app.Services.GetRequiredService<IOptions<PortalOptions>>().Value;
const string Cookie = "ws_session";

app.UseExceptionHandler();
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["Referrer-Policy"] = "strict-origin-when-cross-origin";
    h["X-Frame-Options"] = "DENY";
    h["Content-Security-Policy"] = "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; frame-ancestors 'none'; base-uri 'self'";
    // CSRF: state-changing API calls must carry a custom header (cannot be sent cross-site without CORS, which is off)
    if (ctx.Request.Path.StartsWithSegments("/api") && !HttpMethods.IsGet(ctx.Request.Method) && !ctx.Request.Headers.ContainsKey("X-WS-Portal"))
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsJsonAsync(new { error = "missing X-WS-Portal header" });
        return;
    }
    await next();
});
app.UseRateLimiter();
app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api");

api.MapPost("/auth/login", async (LoginRequest r, SessionStore sessions, HttpContext ctx, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(r.Username) || string.IsNullOrEmpty(r.Password)) return Results.BadRequest(new { error = "username and password are required" });
    PortalSession? s;
    try { s = await sessions.LoginAsync(r.Username.Trim(), r.Password, ct); }
    catch (Exception e) when (e is HttpRequestException or TaskCanceledException or WiseSphere.Glpi.GlpiException)
    {
        app.Logger.LogWarning(e, "GLPI unavailable during login");
        return Results.Json(new { error = "GLPI is not answering" }, statusCode: 503);
    }
    if (s is null) return Results.Json(new { error = "invalid credentials" }, statusCode: 401);
    ctx.Response.Cookies.Append(Cookie, s.Id, new CookieOptions
    {
        HttpOnly = true, Secure = portal.SecureCookies, SameSite = SameSiteMode.Strict, Path = "/", Expires = s.ExpiresAt, IsEssential = true,
    });
    return Results.Ok(Me(s));
}).RequireRateLimiting("login");

api.MapPost("/auth/logout", async (SessionStore sessions, HttpContext ctx, CancellationToken ct) =>
{
    if (ctx.Request.Cookies.TryGetValue(Cookie, out var id)) await sessions.LogoutAsync(id, ct);
    ctx.Response.Cookies.Delete(Cookie);
    return Results.NoContent();
});

// Everything below requires a signed-in user
var secured = api.MapGroup("").AddEndpointFilter(async (ctx, next) =>
{
    var http = ctx.HttpContext;
    var s = http.RequestServices.GetRequiredService<SessionStore>().Get(http.Request.Cookies[Cookie]);
    if (s is null) return Results.Json(new { error = "not signed in" }, statusCode: 401);
    http.Items["session"] = s;
    return await next(ctx);
});
static PortalSession S(HttpContext ctx) => (PortalSession)ctx.Items["session"]!;

secured.MapGet("/me", (HttpContext ctx) => Results.Ok(Me(S(ctx))));
secured.MapGet("/dashboard", async (HttpContext ctx, TicketService t, CancellationToken ct) => Results.Ok(await t.DashboardAsync(S(ctx), ct)));
secured.MapGet("/tickets", async (HttpContext ctx, TicketService t, string? scope, int? page, CancellationToken ct) =>
    Results.Ok(await t.SearchAsync(S(ctx), scope is "solved" or "all" ? scope : "open", Math.Max(0, page ?? 0), 25, ct)));
secured.MapGet("/tickets/{id:int}", async (int id, HttpContext ctx, TicketService t, CancellationToken ct) =>
    await t.GetAsync(S(ctx), id, ct) is { } d ? Results.Ok(d) : Results.NotFound());
secured.MapPost("/tickets/{id:int}/followups", async (int id, FollowupRequest r, HttpContext ctx, TicketService t, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(r.Text) || r.Text.Length > 10_000) return Results.BadRequest(new { error = "text is required (max 10000 characters)" });
    return Results.Ok(new { id = await t.AddFollowupAsync(S(ctx), id, r.Text.Trim(), ct) });
});
secured.MapPost("/tickets", async (NewTicketRequest r, HttpContext ctx, TicketService t, CancellationToken ct) =>
{
    var s = S(ctx);
    if (string.IsNullOrWhiteSpace(r.Title) || r.Title.Length > 250 || string.IsNullOrWhiteSpace(r.Text)) return Results.BadRequest(new { error = "title and description are required" });
    if (!s.Entities.Any(e => e.Id == r.EntityId)) return Results.BadRequest(new { error = "unknown entity" });
    return Results.Ok(new { id = await t.CreateAsync(s, r.EntityId, r.Title.Trim(), r.Text.Trim(), Math.Clamp(r.Urgency, 1, 5), ct) });
});
secured.MapGet("/monitoring/problems", async (HttpContext ctx, MonitoringService m, CancellationToken ct) =>
    Results.Ok(new { enabled = m.Enabled, problems = await m.ProblemsAsync(S(ctx), ct) }));

secured.MapGet("/reports/monthly", async (HttpContext ctx, ReportService r, string client, string month, CancellationToken ct) =>
{
    try { return await r.MonthlyAsync(S(ctx), client, month, ct) is { } rep ? Results.Ok(rep) : Results.NotFound(); }
    catch (ArgumentException e) { return Results.BadRequest(new { error = e.Message }); }
});

// SOC-04: approved containment on a security ticket - SOC group only
secured.MapGet("/tickets/{id:int}/containment", async (int id, HttpContext ctx, ContainmentClient c, CancellationToken ct) =>
{
    var s = S(ctx);
    if (!s.IsSoc) return Results.Ok(new { allowed = false, blocks = Array.Empty<object>() });
    return Results.Ok(new { allowed = c.Enabled, blocks = c.Enabled ? await c.ListAsync(id, ct) : default });
});
secured.MapPost("/tickets/{id:int}/containment", async (int id, BlockApproval r, HttpContext ctx, TicketService tickets, ContainmentClient c, CancellationToken ct) =>
{
    var s = S(ctx);
    if (!s.IsSoc) return Results.Json(new { errors = new[] { "only the SOC group can approve containment" } }, statusCode: 403);
    var t = await tickets.GetAsync(s, id, ct);
    if (t is null || !t.Ticket.Title.StartsWith("[WZH:", StringComparison.Ordinal))
        return Results.BadRequest(new { errors = new[] { "containment is only possible on a security (Wazuh) ticket" } });
    var (status, body) = await c.BlockAsync(new { ticketId = id, clientCode = t.Ticket.Client, ip = r.Ip, hours = r.Hours, approvedBy = s.Login, reason = r.Reason }, ct);
    return Results.Json(body, statusCode: (int)status);
});
secured.MapDelete("/containment/{blockId}", async (string blockId, HttpContext ctx, ContainmentClient c, CancellationToken ct) =>
{
    var s = S(ctx);
    if (!s.IsSoc) return Results.StatusCode(403);
    return await c.UnblockAsync(blockId, s.Login, ct) ? Results.NoContent() : Results.NotFound();
});

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
app.MapFallbackToFile("index.html"); // Angular routes

app.Run();

static object Me(PortalSession s) => new
{
    name = s.DisplayName, login = s.Login, isStaff = s.IsStaff, isSoc = s.IsSoc,
    entities = s.Entities.Select(e => new { e.Id, e.Name, clientCode = e.ClientCode }),
};

internal sealed record LoginRequest(string Username, string Password);
internal sealed record FollowupRequest(string Text);
internal sealed record BlockApproval(string Ip, int Hours, string Reason);
internal sealed record NewTicketRequest(int EntityId, string Title, string Text, int Urgency = 3);

/// <summary>Exposed for integration tests.</summary>
public partial class Program;
