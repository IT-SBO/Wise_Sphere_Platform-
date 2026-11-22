using WiseSphere.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace WiseSphere.Integration.Services;

/// <summary>Loads config/tenants.yaml and config/sla.yaml (the same files the provisioning scripts use).</summary>
public static class PolicyLoader
{
    private sealed class TenantsFile { public List<TenantYaml> Tenants { get; set; } = []; }
    private sealed class TenantYaml
    {
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public string Tier { get; set; } = "silver";
        public string Country { get; set; } = "KW";
        public string Language { get; set; } = "en_GB";
        public List<SiteYaml> Sites { get; set; } = [];
        public ContainmentYaml? Containment { get; set; }
    }
    private sealed class ContainmentYaml
    {
        public bool Enabled { get; set; }
        public string FirewallType { get; set; } = "log-only";
        public string FirewallUrl { get; set; } = "";
        public string AddressGroup { get; set; } = "WS-Blocklist";
        public List<string> AllowList { get; set; } = [];
        public int MaxHours { get; set; } = 24;
    }
    private sealed class SiteYaml { public string Code { get; set; } = ""; public string WgSubnet { get; set; } = ""; public string SpokeSize { get; set; } = "small"; }
    private sealed class SlaFile
    {
        public Dictionary<string, CalendarYaml> Calendars { get; set; } = [];
        public Dictionary<string, TierYaml> Tiers { get; set; } = [];
        public int AtRiskMinutes { get; set; } = 15;
    }
    private sealed class CalendarYaml { public string Name { get; set; } = ""; public string Timezone { get; set; } = "UTC"; public List<int> Days { get; set; } = []; public string Start { get; set; } = "08:00"; public string End { get; set; } = "17:00"; }
    private sealed class TierYaml { public string Label { get; set; } = ""; public List<int> AfterHoursPriorities { get; set; } = []; public Dictionary<int, TargetYaml> Priorities { get; set; } = []; }
    private sealed class TargetYaml { public int Tto { get; set; } public int Ttr { get; set; } public bool BusinessHours { get; set; } }

    public static PlatformPolicy Load(string configDir)
    {
        var yaml = new DeserializerBuilder().WithNamingConvention(UnderscoredNamingConvention.Instance).IgnoreUnmatchedProperties().Build();
        var tenants = yaml.Deserialize<TenantsFile>(File.ReadAllText(Path.Combine(configDir, "tenants.yaml")));
        var sla = yaml.Deserialize<SlaFile>(File.ReadAllText(Path.Combine(configDir, "sla.yaml")));

        static Tier ParseTier(string t) => Enum.Parse<Tier>(t, ignoreCase: true);
        return new PlatformPolicy(
            tenants.Tenants.ToDictionary(t => t.Code, t => new Tenant(t.Code, t.Name, ParseTier(t.Tier), t.Country, t.Language,
                t.Sites.Select(s => new Site(s.Code, s.WgSubnet, s.SpokeSize)).ToList())),
            sla.Tiers.ToDictionary(kv => ParseTier(kv.Key), kv => new TierPolicy(ParseTier(kv.Key), kv.Value.Label, kv.Value.AfterHoursPriorities,
                kv.Value.Priorities.ToDictionary(p => p.Key, p => new SlaTarget(p.Value.Tto, p.Value.Ttr, p.Value.BusinessHours)))),
            sla.Calendars.ToDictionary(kv => kv.Key, kv => new BusinessCalendar(kv.Value.Name, kv.Value.Timezone, kv.Value.Days,
                TimeOnly.Parse(kv.Value.Start), TimeOnly.Parse(kv.Value.End))),
            sla.AtRiskMinutes,
            tenants.Tenants.Where(t => t.Containment is not null).ToDictionary(t => t.Code, t => new ContainmentSettings(
                t.Containment!.Enabled, t.Containment.FirewallType, t.Containment.FirewallUrl, t.Containment.AddressGroup,
                t.Containment.AllowList, Math.Min(24, t.Containment.MaxHours))));
    }
}
