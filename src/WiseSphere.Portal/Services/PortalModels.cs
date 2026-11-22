namespace WiseSphere.Portal.Services;

public sealed class PortalOptions
{
    public const string Section = "Portal";
    /// <summary>Time zone GLPI uses for the dates it returns (GLPI "timezone" setting).</summary>
    public string GlpiTimeZone { get; set; } = "Asia/Kuwait";
    /// <summary>Public GLPI URL, used for "open in GLPI" links.</summary>
    public string GlpiPublicUrl { get; set; } = "http://localhost:8090";
    /// <summary>Send the session cookie only over HTTPS (false only in the lab).</summary>
    public bool SecureCookies { get; set; } = true;
    public int SessionHours { get; set; } = 8;
    /// <summary>Minutes before the resolve deadline at which a ticket counts as "at risk" on the dashboard.</summary>
    public int AtRiskMinutes { get; set; } = 60;
}

public sealed class ZabbixOptions
{
    public const string Section = "Zabbix";
    public string ApiUrl { get; set; } = "";
    /// <summary>Read-only API token (Zabbix user with read access to the CLnnn/* host groups).</summary>
    public string ApiToken { get; set; } = "";
}

public sealed record EntityInfo(int Id, string Name, string ClientCode);

/// <summary>Server-side portal session. Only its id travels in the cookie; the GLPI token never reaches the browser.</summary>
public sealed record PortalSession(string Id, string GlpiToken, int UserId, string Login, string DisplayName, bool IsStaff,
    IReadOnlyList<EntityInfo> Entities, DateTimeOffset ExpiresAt)
{
    public IEnumerable<string> ClientCodes => Entities.Select(e => e.ClientCode).Where(c => c.Length > 0).Distinct();
}

public sealed record TicketRow(int Id, string Title, int Status, int Priority, string Client, string Entity,
    DateTimeOffset? OpenedAt, DateTimeOffset? DueAt, DateTimeOffset? UpdatedAt, string Source);

public sealed record TimelineItem(string Kind, DateTimeOffset? At, string Author, string Content, bool IsPrivate);

public sealed record TicketDetail(TicketRow Ticket, string Content, IReadOnlyList<TimelineItem> Timeline, string GlpiUrl);

public sealed record ClientHealth(string Client, string Name, int OpenP1, int OpenP2, int Open, int Breached, int AtRisk, string Status);

public sealed record Dashboard(int Open, int OpenP1, int OpenP2, int AtRisk, int Breached, int Storms,
    IReadOnlyList<ClientHealth> Clients, IReadOnlyList<TicketRow> Urgent, IReadOnlyList<TicketRow> Recent);

public sealed record Problem(string EventId, string Name, int Severity, string Host, string Client, DateTimeOffset Since, bool Acknowledged);
