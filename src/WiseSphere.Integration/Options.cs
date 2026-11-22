namespace WiseSphere.Integration;

/// <summary>Settings of the integration service (appsettings.json, overridable with WS__* environment variables).</summary>
public sealed class IntegrationOptions
{
    public const string Section = "Integration";

    /// <summary>Shared secret expected in the X-WS-Secret header of every webhook (INT-05).</summary>
    public string WebhookSecret { get; set; } = "";
    /// <summary>Folder holding tenants.yaml and sla.yaml.</summary>
    public string ConfigDir { get; set; } = "/config";
    /// <summary>Folder for small persistent state (SMS dedupe, processed surveys).</summary>
    public string StateDir { get; set; } = "/data";
    public int DefaultEntityId { get; set; }
    public int StormThreshold { get; set; } = 10;
    public int StormWindowMinutes { get; set; } = 5;
    public int WazuhTicketLevel { get; set; } = 10;
    public int WazuhPageLevel { get; set; } = 12;
    public int CsatLowScore { get; set; } = 2;
    public bool SlaWatchEnabled { get; set; } = true;
    public int SlaWatchIntervalMinutes { get; set; } = 5;
    public bool CsatReviewEnabled { get; set; } = true;
    /// <summary>Local time (calendar of the first tenant country) for the daily CSAT review.</summary>
    public string CsatReviewTime { get; set; } = "08:10";

    /// <summary>Ids of GLPI items created by glpi/provision_glpi.py (written to its --out file).
    /// When an id is 0 the service looks it up by name; that needs read rights on dropdowns, which the
    /// least-privilege Technician profile does not have - so production passes the ids explicitly.</summary>
    public GlpiIds Ids { get; set; } = new();
    public GlpiNames Names { get; set; } = new();
}

public sealed class GlpiIds
{
    public int RequestTypeMonitoring { get; set; }
    public int RequestTypeSecurity { get; set; }
    public int GroupNoc { get; set; }
    public int GroupSoc { get; set; }
    public int GroupSdm { get; set; }
    public int SolutionType { get; set; }
}

public sealed class GlpiNames
{
    public string RequestTypeMonitoring { get; set; } = "Monitoring";
    public string RequestTypeSecurity { get; set; } = "Security";
    public string GroupNoc { get; set; } = "WS-NOC";
    public string GroupSoc { get; set; } = "WS-SOC";
    public string GroupSdm { get; set; } = "WS-SDM";
    public string SolutionType { get; set; } = "Auto-recovered (monitoring)";
}

/// <summary>SMS provider (decision D-04). Any HTTP API accepting POST {to, message, source} with a key header.</summary>
public sealed class SmsOptions
{
    public const string Section = "Sms";
    public string Url { get; set; } = "";
    public string KeyHeader { get; set; } = "X-SMS-Key";
    public string Key { get; set; } = "";
    /// <summary>On-call numbers per channel (E.164): noc, soc, desk, integration. From the rota (SOP-05).</summary>
    public Dictionary<string, string> Numbers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
