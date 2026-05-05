using System.Net;
using FluentAssertions;
using Xunit;

namespace Verbara.Platform.Identity.Tests;

public class DefaultIpAllowlistEvaluatorTests
{
    private readonly DefaultIpAllowlistEvaluator _sut = new();

    private static IpAllowlistEntry Entry(string cidr) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = "t1",
        Cidr = cidr,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void IsAllowed_ShouldReturnTrue_WhenIpv4InRange()
    {
        var entries = new[] { Entry("192.0.2.0/24") };
        _sut.IsAllowed(IPAddress.Parse("192.0.2.45"), entries).Should().BeTrue();
    }

    [Fact]
    public void IsAllowed_ShouldReturnFalse_WhenIpv4OutOfRange()
    {
        var entries = new[] { Entry("192.0.2.0/24") };
        _sut.IsAllowed(IPAddress.Parse("203.0.113.5"), entries).Should().BeFalse();
    }

    [Fact]
    public void IsAllowed_ShouldReturnTrue_WhenIpv6InRange()
    {
        var entries = new[] { Entry("2001:db8::/32") };
        _sut.IsAllowed(IPAddress.Parse("2001:db8:1234::5"), entries).Should().BeTrue();
    }

    [Fact]
    public void IsAllowed_ShouldReturnFalse_WhenEntriesEmpty()
    {
        _sut.IsAllowed(IPAddress.Parse("192.0.2.45"), Array.Empty<IpAllowlistEntry>()).Should().BeFalse();
    }

    [Fact]
    public void IsAllowed_ShouldReturnFalse_WhenAddressFamilyMismatch()
    {
        var entries = new[] { Entry("2001:db8::/32") };
        _sut.IsAllowed(IPAddress.Parse("192.0.2.45"), entries).Should().BeFalse();
    }

    [Fact]
    public void IsAllowed_ShouldReturnTrue_WhenExactSingleHost()
    {
        var entries = new[] { Entry("192.0.2.45/32") };
        _sut.IsAllowed(IPAddress.Parse("192.0.2.45"), entries).Should().BeTrue();
    }

    [Fact]
    public void IsAllowed_ShouldThrow_WhenCidrMalformed()
    {
        var entries = new[] { Entry("not-a-cidr") };
        Action act = () => _sut.IsAllowed(IPAddress.Parse("192.0.2.45"), entries);
        act.Should().Throw<FormatException>();
    }
}
