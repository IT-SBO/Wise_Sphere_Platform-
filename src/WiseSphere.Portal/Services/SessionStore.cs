using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using WiseSphere.Glpi;

namespace WiseSphere.Portal.Services;

/// <summary>Logs users in against GLPI and keeps their GLPI session server-side.
/// Staff = users with access to the root entity (WISE NOC/SOC); client users only have their own client entity.</summary>
public sealed class SessionStore(GlpiClient glpi, IOptions<PortalOptions> options, ILogger<SessionStore> log)
{
    private readonly ConcurrentDictionary<string, PortalSession> _sessions = new(StringComparer.Ordinal);

    public async Task<PortalSession?> LoginAsync(string login, string password, CancellationToken ct)
    {
        var token = await glpi.LoginUserAsync(login, password, ct);
        if (token is null) { log.LogWarning("Portal login failed for {Login}", login); return null; }

        var full = await glpi.UserRequestAsync(token, HttpMethod.Get, "/getFullSession", null, ct);
        var s = full!.Value.GetProperty("session");
        var userId = s.GetProperty("glpiID").GetInt32();
        var display = string.Join(" ", new[] { Str(s, "glpifirstname"), Str(s, "glpirealname") }.Where(x => x.Length > 0));

        // A new GLPI session is already scoped to the user's default entity, recursively (staff: root; client: own entity).
        // Note: GLPI 11 does not accept entities_id "all" - it would narrow the scope to the root entity alone.
        var mine = await glpi.UserRequestAsync(token, HttpMethod.Get, "/getMyEntities" + GlpiClient.Query(("is_recursive", "1")), null, ct);
        var entities = new List<EntityInfo>();
        if (mine is { } m && m.TryGetProperty("myentities", out var list))
            foreach (var e in list.EnumerateArray())
            {
                var name = e.GetProperty("name").GetString() ?? "";
                entities.Add(new(e.GetProperty("id").GetInt32(), name, ClientCode(name)));
            }

        var session = new PortalSession(
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), token, userId, login,
            display.Length > 0 ? display : login, entities.Any(e => e.Id == 0), entities,
            DateTimeOffset.UtcNow.AddHours(options.Value.SessionHours));
        _sessions[session.Id] = session;
        log.LogInformation("Portal login {Login} staff={Staff} clients={Clients}", login, session.IsStaff, string.Join(",", session.ClientCodes));
        return session;
    }

    public PortalSession? Get(string? id)
    {
        if (id is null || !_sessions.TryGetValue(id, out var s)) return null;
        if (s.ExpiresAt > DateTimeOffset.UtcNow) return s;
        _sessions.TryRemove(id, out _);
        return null;
    }

    public async Task LogoutAsync(string id, CancellationToken ct)
    {
        if (_sessions.TryRemove(id, out var s)) await glpi.LogoutUserAsync(s.GlpiToken, ct);
    }

    /// <summary>"WiseSphere > CL001 - Pilot Client" or "CL001 - Pilot Client" -> "CL001"</summary>
    public static string ClientCode(string entityName)
    {
        var last = entityName.Split('>').Last().Trim();
        return last.Length >= 5 && last.StartsWith("CL", StringComparison.Ordinal) && last[2..5].All(char.IsAsciiDigit) ? last[..5] : "";
    }

    private static string Str(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
