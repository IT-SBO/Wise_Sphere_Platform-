namespace WiseSphere.Core;

/// <summary>GLPI ticket status values.</summary>
public static class TicketStatus
{
    public const int New = 1, Assigned = 2, Planned = 3, Waiting = 4, Solved = 5, Closed = 6;
    public static bool IsOpen(int status) => status < Solved;
}

/// <summary>Minimal view of a GLPI ticket as returned by searches.</summary>
public sealed record TicketRef(int Id, string Name, int Status);

/// <summary>One write to the GLPI REST API: POST {Path} with body {"input": Input}.</summary>
public sealed record GlpiWrite(string Path, IReadOnlyDictionary<string, object?> Input);

/// <summary>SMS to send. Channel = noc | soc | desk | integration (resolved to numbers from the on-call config).</summary>
public sealed record Notification(string Channel, string Message);

/// <summary>Result of a decider: what to write to GLPI and whom to page.</summary>
public sealed record Decision(string Action, IReadOnlyList<GlpiWrite> Writes, Notification? Notify, int? TicketId = null, string? Reason = null)
{
    public static Decision Noop(string reason) => new("noop", [], null, null, reason);
}

/// <summary>Package tiers from PRD §8.</summary>
public enum Tier { Silver, Gold, Platinum }

public sealed record Site(string Code, string WgSubnet, string SpokeSize);

public sealed record Tenant(string Code, string Name, Tier Tier, string Country, string Language, IReadOnlyList<Site> Sites);

public sealed record BusinessCalendar(string Name, string TimeZone, IReadOnlyList<int> Days, TimeOnly Start, TimeOnly End)
{
    /// <summary>True when <paramref name="utcNow"/> falls inside the calendar's working hours (local time).</summary>
    public bool IsOpen(DateTimeOffset utcNow)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(TimeZone);
        var local = TimeZoneInfo.ConvertTime(utcNow, tz);
        var t = TimeOnly.FromDateTime(local.DateTime);
        return Days.Contains((int)local.DayOfWeek) && t >= Start && t < End;
    }
}

public sealed record SlaTarget(int TimeToOwnMinutes, int TimeToResolveMinutes, bool BusinessHours);

public sealed record TierPolicy(Tier Tier, string Label, IReadOnlyList<int> AfterHoursPriorities, IReadOnlyDictionary<int, SlaTarget> Priorities);

/// <summary>Everything loaded from config/tenants.yaml and config/sla.yaml.</summary>
public sealed record PlatformPolicy(
    IReadOnlyDictionary<string, Tenant> Tenants,
    IReadOnlyDictionary<Tier, TierPolicy> Tiers,
    IReadOnlyDictionary<string, BusinessCalendar> Calendars,
    int AtRiskMinutes,
    IReadOnlyDictionary<string, ContainmentSettings>? Containment = null);
