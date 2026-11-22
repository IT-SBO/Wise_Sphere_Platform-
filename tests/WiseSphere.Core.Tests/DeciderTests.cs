using System.Text.Json;
using WiseSphere.Core;

namespace WiseSphere.Core.Tests;

public class ZabbixTests
{
    private static readonly ZabbixDecisionSettings Cfg = new(DefaultEntityId: 0, StormThreshold: 10, RequestTypeId: 7, NocGroupId: 3);

    private static ZabbixEvent Ev(object? overrides = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["schema"] = "ws.event.v1", ["event_id"] = "1001", ["status"] = "PROBLEM", ["severity"] = "4",
            ["event_name"] = "Interface Gi0/1 down", ["host"] = "cl001-kwi-sw01", ["host_name"] = "Core switch",
            ["tags"] = """[{"tag":"client","value":"cl001"},{"tag":"site","value":"kwi"},{"tag":"glpi_asset","value":"NetworkEquipment:12"}]""",
        };
        if (overrides is not null)
            foreach (var p in overrides.GetType().GetProperties()) body[p.Name] = p.GetValue(overrides);
        var r = ZabbixEventParser.Parse(JsonSerializer.SerializeToElement(body));
        Assert.True(r.IsValid, string.Join(", ", r.Errors));
        return r.Value!;
    }

    [Theory]
    [InlineData("{EVENT.TAGS.client}", "")]
    [InlineData("*UNKNOWN*", "")]
    [InlineData("  CL001 ", "CL001")]
    [InlineData(null, "")]
    public void Clean_drops_unresolved_macros(string? input, string expected) => Assert.Equal(expected, Text.Clean(input));

    [Fact]
    public void Parse_reads_client_site_asset_from_tags()
    {
        var e = Ev();
        Assert.Equal("CL001", e.ClientCode);
        Assert.Equal("KWI", e.SiteCode);
        Assert.Equal("NetworkEquipment:12", e.GlpiAsset);
    }

    [Fact]
    public void Parse_accepts_poc_payload()
    {
        var body = JsonSerializer.SerializeToElement(new { subject = "Zabbix agent is not available", host = "Zabbix server", severity = "High", eventid = "42", event_value = "1" });
        var r = ZabbixEventParser.Parse(body);
        Assert.True(r.IsValid);
        Assert.Equal(4, r.Value!.Severity);
        Assert.Equal(ZabbixStatus.Problem, r.Value.Status);
        Assert.Equal("", r.Value.ClientCode);
    }

    [Fact]
    public void Parse_reports_all_errors()
    {
        var r = ZabbixEventParser.Parse(JsonSerializer.SerializeToElement(new { event_id = "abc", status = "X" }));
        Assert.False(r.IsValid);
        Assert.True(r.Errors.Count >= 3);
        Assert.False(ZabbixEventParser.Parse(JsonSerializer.SerializeToElement(new { event_id = "1", status = "PROBLEM", severity = "3", event_name = "x", host = "h", client_code = "ACME" })).IsValid);
    }

    [Fact]
    public void Parse_maps_event_value_zero_to_resolved_and_update_flag()
    {
        Assert.Equal(ZabbixStatus.Resolved, Ev(new { status = (string?)null, event_value = "0" }).Status);
        Assert.Equal(ZabbixStatus.Update, Ev(new { status = (string?)null, event_value = "1", event_update = "1" }).Status);
    }

    [Theory]
    [InlineData(5, 5, 5)]
    [InlineData(4, 4, 4)]
    [InlineData(3, 3, 3)]
    [InlineData(1, 2, 2)]
    public void Severity_mapping_INT01(int sev, int urgency, int impact) => Assert.Equal((urgency, impact), ZabbixDecider.MapSeverity(sev));

    [Fact]
    public void TC01_new_problem_creates_ticket_in_client_entity_with_asset_link()
    {
        var d = ZabbixDecider.Decide(Ev(), 5, [], [], 0, Cfg);
        Assert.Equal("create", d.Action);
        var input = d.Writes[0].Input;
        Assert.Equal("/Ticket", d.Writes[0].Path);
        Assert.Equal(5, input["entities_id"]);
        Assert.StartsWith("[ZBX:1001] Core switch: Interface Gi0/1 down", (string)input["name"]!);
        Assert.Equal(12, ((Dictionary<string, int[]>)input["items_id"]!)["NetworkEquipment"][0]);
        Assert.Equal(4, input["urgency"]);
        Assert.Equal(7, input["requesttypes_id"]);
        Assert.Equal(3, input["_groups_id_assign"]);
        Assert.Null(d.Notify);
    }

    [Fact]
    public void Disaster_pages_noc() => Assert.Equal("noc", ZabbixDecider.Decide(Ev(new { severity = "5" }), 5, [], [], 0, Cfg).Notify?.Channel);

    [Fact]
    public void Unmapped_host_goes_to_default_entity_and_never_storms()
    {
        var d = ZabbixDecider.Decide(Ev(new { tags = "" }), null, [], [], 50, Cfg);
        Assert.Equal("create", d.Action);
        Assert.Equal(0, d.Writes[0].Input["entities_id"]);
        Assert.Contains("UNMAPPED", (string)d.Writes[0].Input["content"]!);
    }

    [Fact]
    public void TC02_recovery_solves_open_ticket()
    {
        var d = ZabbixDecider.Decide(Ev(new { status = "RESOLVED" }), 5, [new(77, "[ZBX:1001] Core switch", 2)], [], 0, Cfg);
        Assert.Equal("solve", d.Action);
        Assert.Equal("/ITILSolution", d.Writes[0].Path);
        Assert.Equal(77, d.Writes[0].Input["items_id"]);
    }

    [Fact]
    public void Recovery_for_solved_ticket_is_noop() =>
        Assert.Equal("noop", ZabbixDecider.Decide(Ev(new { status = "RESOLVED" }), 5, [new(77, "[ZBX:1001] x", 5)], [], 0, Cfg).Action);

    [Fact]
    public void TC03_repeat_adds_followup()
    {
        var d = ZabbixDecider.Decide(Ev(), 5, [new(77, "[ZBX:1001] Core switch", 1)], [], 3, Cfg);
        Assert.Equal("followup", d.Action);
        Assert.Equal("/ITILFollowup", d.Writes[0].Path);
    }

    [Fact]
    public void Key_match_is_exact() => Assert.Null(Tickets.FindByKey([new(1, "[ZBX:1001] a", 1)], "[ZBX:100]"));

    [Fact]
    public void TC04_threshold_creates_storm_parent_and_pages()
    {
        var d = ZabbixDecider.Decide(Ev(), 5, [], [], 10, Cfg);
        Assert.Equal("storm_create", d.Action);
        Assert.StartsWith("[STORM:CL001]", (string)d.Writes[0].Input["name"]!);
        Assert.Equal("noc", d.Notify?.Channel);
    }

    [Fact]
    public void TC04_open_storm_gets_followups_solved_storm_is_ignored()
    {
        Assert.Equal("storm_followup", ZabbixDecider.Decide(Ev(), 5, [], [new(90, "[STORM:CL001] storm", 1)], 12, Cfg).Action);
        Assert.Equal("storm_create", ZabbixDecider.Decide(Ev(), 5, [], [new(90, "[STORM:CL001] storm", 5)], 12, Cfg).Action);
    }

    [Fact]
    public void Recovery_of_grouped_event_goes_to_storm() =>
        Assert.Equal("storm_followup", ZabbixDecider.Decide(Ev(new { status = "RESOLVED" }), 5, [], [new(90, "[STORM:CL001] x", 2)], 0, Cfg).Action);

    [Fact]
    public void Content_is_html_escaped() =>
        Assert.DoesNotContain("<script>", (string)ZabbixDecider.Decide(Ev(new { event_name = "<script>alert(1)</script>" }), 5, [], [], 0, Cfg).Writes[0].Input["content"]!);
}

public class SecurityTests
{
    private static SecurityAlert Alert(int level = 10, string rule = "5712")
    {
        var body = JsonSerializer.SerializeToElement(new
        {
            schema = "ws.secevent.v1", alert_id = "1700000000.123", timestamp = "2026-11-22T10:00:00Z", srcip = "203.0.113.5",
            rule = new { id = rule, level, description = "sshd: brute force", groups = new[] { "authentication_failures" }, mitre = new { id = new[] { "T1110" } } },
            agent = new { id = "003", name = "cl001-dc01", ip = "10.1.1.10", labels = new { client = "CL001", site = "KWI" } },
        });
        var r = SecurityAlertParser.Parse(body);
        Assert.True(r.IsValid, string.Join(", ", r.Errors));
        return r.Value!;
    }

    [Theory]
    [InlineData(13, 5, 5)]
    [InlineData(12, 4, 4)]
    [InlineData(10, 4, 3)]
    public void Level_mapping_INT02(int level, int u, int i) => Assert.Equal((u, i), SecurityDecider.MapLevel(level));

    [Fact]
    public void TC07_level10_ticket_without_sms_level12_pages_soc()
    {
        var s = new SecurityDecisionSettings(SocGroupId: 4, RequestTypeId: 8);
        var d10 = SecurityDecider.Decide(Alert(), 5, [], s);
        Assert.Equal("create", d10.Action);
        Assert.StartsWith("[WZH:003:5712]", (string)d10.Writes[0].Input["name"]!);
        Assert.Equal(4, d10.Writes[0].Input["_groups_id_assign"]);
        Assert.Null(d10.Notify);
        Assert.Equal("soc", SecurityDecider.Decide(Alert(12), 5, [], s).Notify?.Channel);
    }

    [Fact]
    public void Dedupe_on_open_ticket_and_low_level_ignored()
    {
        Assert.Equal("followup", SecurityDecider.Decide(Alert(), 5, [new(3, "[WZH:003:5712] x", 2)], new()).Action);
        Assert.Equal("noop", SecurityDecider.Decide(Alert(5, "1"), 5, [], new()).Action);
    }
}

public class SlaTests
{
    private static readonly PlatformPolicy Policy = new(
        new Dictionary<string, Tenant>
        {
            ["CL001"] = new("CL001", "Pilot", Tier.Gold, "KW", "ar_SA", []),
            ["CL002"] = new("CL002", "Other", Tier.Silver, "KW", "en_GB", []),
        },
        new Dictionary<Tier, TierPolicy>
        {
            [Tier.Silver] = new(Tier.Silver, "Silver", [5, 6], new Dictionary<int, SlaTarget>()),
            [Tier.Gold] = new(Tier.Gold, "Gold", [4, 5, 6], new Dictionary<int, SlaTarget>()),
        },
        new Dictionary<string, BusinessCalendar> { ["KW"] = new("KW", "Asia/Kuwait", [0, 1, 2, 3, 4], new(8, 0), new(17, 0)) },
        15);

    [Fact]
    public void Business_hours_in_kuwait()
    {
        var cal = Policy.Calendars["KW"];
        Assert.True(cal.IsOpen(DateTimeOffset.Parse("2026-11-22T07:00:00Z")));   // Sun 10:00
        Assert.False(cal.IsOpen(DateTimeOffset.Parse("2026-11-22T20:00:00Z")));  // Sun 23:00
        Assert.False(cal.IsOpen(DateTimeOffset.Parse("2026-11-27T07:00:00Z")));  // Friday
    }

    [Fact]
    public void TC06_tier_aware_after_hours_and_deduped()
    {
        var night = DateTimeOffset.Parse("2026-11-22T20:00:00Z");
        SlaCandidate[] rows =
        [
            new(1, "Gold P2", 4, "WiseSphere > CL001 - Pilot", SlaKind.TimeToResolve, SlaStage.AtRisk),
            new(2, "Silver P2", 4, "WiseSphere > CL002 - Other", SlaKind.TimeToResolve, SlaStage.AtRisk),
            new(3, "Silver P1", 5, "WiseSphere > CL002 - Other", SlaKind.TimeToOwn, SlaStage.Breached),
        ];
        var sent = new Dictionary<string, DateTimeOffset>();
        var a1 = SlaDecider.Decide(rows, Policy, sent, night);
        Assert.Equal([1, 3], a1.Select(a => a.TicketId));
        Assert.Contains("AT RISK CL001 GOLD P2 TTR #1", a1[0].Notification.Message);
        Assert.Equal("noc", a1[0].Notification.Channel);
        Assert.Empty(SlaDecider.Decide(rows, Policy, sent, night));
        Assert.Single(SlaDecider.Decide([rows[0] with { Stage = SlaStage.Breached }], Policy, sent, night));
    }

    [Fact]
    public void In_business_hours_goes_to_desk_for_any_priority()
    {
        var day = DateTimeOffset.Parse("2026-11-22T07:00:00Z");
        var a = SlaDecider.Decide([new(9, "P3", 3, "WiseSphere > CL002 - Other", SlaKind.TimeToResolve, SlaStage.AtRisk)], Policy, new Dictionary<string, DateTimeOffset>(), day);
        Assert.Equal("desk", Assert.Single(a).Notification.Channel);
    }

    [Fact]
    public void Dedupe_memory_is_pruned_after_three_days()
    {
        var now = DateTimeOffset.Parse("2026-11-22T07:00:00Z");
        var sent = new Dictionary<string, DateTimeOffset> { ["old"] = now.AddDays(-4), ["new"] = now };
        SlaDecider.Decide([], Policy, sent, now);
        Assert.Equal(["new"], sent.Keys);
    }

    [Fact]
    public void Csat_only_new_low_scores_create_tasks()
    {
        SurveyAnswer[] rows = [new(1, 10, 1, "slow"), new(2, 11, 5, null), new(3, 12, null, null)];
        var processed = new HashSet<int>();
        var w = CsatDecider.Decide(rows, processed, sdmGroupId: 3);
        Assert.Equal(10, Assert.Single(w).Input["tickets_id"]);
        Assert.Empty(CsatDecider.Decide(rows, processed, 3));
    }
}
