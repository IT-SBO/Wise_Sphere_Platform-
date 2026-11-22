using System.Net;
using WiseSphere.Core;

namespace WiseSphere.Core.Tests;

public class ContainmentTests
{
    private static readonly ContainmentSettings On = new(true, "log-only", "", "WS-Blocklist", ["203.0.113.0/28"], 24);
    private static BlockRequest Req(string ip = "198.51.100.7", int hours = 24) => new(16, "CL001", ip, hours, "soc.engineer", "Brute force from this IP");

    [Fact]
    public void Valid_request_passes() => Assert.Empty(ContainmentPolicy.Validate(Req(), On, []));

    [Fact]
    public void Client_must_have_opted_in()
    {
        Assert.Contains(ContainmentPolicy.Validate(Req(), null, []), e => e.Contains("not enabled"));
        Assert.Contains(ContainmentPolicy.Validate(Req(), On with { Enabled = false }, []), e => e.Contains("not enabled"));
    }

    [Theory]
    [InlineData("10.1.1.10")]
    [InlineData("192.168.1.1")]
    [InlineData("172.20.0.5")]
    [InlineData("127.0.0.1")]
    [InlineData("100.64.1.1")]
    [InlineData("fd00::1")]
    public void Internal_addresses_are_never_blocked(string ip) =>
        Assert.Contains(ContainmentPolicy.Validate(Req(ip), On, []), e => e.Contains("internal"));

    [Fact]
    public void Allow_list_is_respected() =>
        Assert.Contains(ContainmentPolicy.Validate(Req("203.0.113.5"), On, []), e => e.Contains("allow-list"));

    [Theory]
    [InlineData(0)]
    [InlineData(25)]
    public void Duration_is_capped_at_24h(int hours) =>
        Assert.Contains(ContainmentPolicy.Validate(Req(hours: hours), On, []), e => e.Contains("duration"));

    [Fact]
    public void Approver_reason_and_valid_ip_required()
    {
        var errors = ContainmentPolicy.Validate(new(16, "CL001", "not-an-ip", 4, "", ""), On, []);
        Assert.Contains(errors, e => e.Contains("approving"));
        Assert.Contains(errors, e => e.Contains("reason"));
        Assert.Contains(errors, e => e.Contains("valid IP"));
    }

    [Fact]
    public void Duplicate_block_rejected_and_expiry_found()
    {
        var now = DateTimeOffset.Parse("2026-11-22T10:00:00Z");
        var b = new ActiveBlock("1", 16, "CL001", "198.51.100.7", now.AddHours(-25), now.AddHours(-1), "soc", "x", "log-only");
        Assert.Contains(ContainmentPolicy.Validate(Req(), On, [b]), e => e.Contains("already blocked"));
        Assert.Single(ContainmentPolicy.Expired([b, b with { Id = "2", ExpiresAt = now.AddHours(1) }], now));
    }

    [Theory]
    [InlineData("198.51.100.7", "198.51.100.0/24", true)]
    [InlineData("198.51.101.7", "198.51.100.0/24", false)]
    [InlineData("198.51.100.7", "198.51.100.7", true)]
    [InlineData("2001:db8::1", "2001:db8::/32", true)]
    public void Cidr_matching(string ip, string cidr, bool expected) =>
        Assert.Equal(expected, ContainmentPolicy.InCidr(IPAddress.Parse(ip), cidr));
}
