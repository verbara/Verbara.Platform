using Verbara.Platform.Api.Services.OutboundTrunk;
using Trunk = Verbara.Sdk.Pro.Dialer.Models.Trunk;

namespace Verbara.Platform.Api.Tests.OutboundTrunk;

/// <summary>
/// outbound-calls-reach-the-trunk (design D3): the dial host of a trunk is its registration URI's host (and port) first,
/// then a <c>MatchHost</c> that names exactly one address; anything else is not dialable.
/// </summary>
public sealed class DialHostTests
{
    [Theory]
    [InlineData(null, "203.0.113.10", "203.0.113.10")]
    [InlineData(null, "203.0.113.10/32", "203.0.113.10")]
    [InlineData(null, "203.0.113.0/24", null)]
    [InlineData(null, "2001:db8::1", "[2001:db8::1]")]
    [InlineData(null, "2001:db8::1/128", "[2001:db8::1]")]
    [InlineData(null, "2001:db8::/64", null)]
    [InlineData(null, "carrier.example.net", null)]
    [InlineData(null, "203.0.113.10/abc", null)]
    [InlineData(null, " ", null)]
    [InlineData("sip:carrier.example.net:5070", null, "carrier.example.net:5070")]
    [InlineData("sip:user@carrier.example.net;transport=udp", null, "carrier.example.net")]
    [InlineData("SIPS:[2001:db8::2]:5061", null, "[2001:db8::2]:5061")]
    [InlineData("sip:carrier.example.net?X-Header=1", null, "carrier.example.net")]
    [InlineData("sip:registrar.example.net", "203.0.113.10", "registrar.example.net")]
    [InlineData("http://carrier.example.net", "203.0.113.10", "203.0.113.10")]
    [InlineData("sip:", "203.0.113.10", "203.0.113.10")]
    [InlineData("sip:user@", "203.0.113.10", "203.0.113.10")]
    [InlineData("sip:[2001:db8::2", "203.0.113.10", "203.0.113.10")]
    [InlineData("sip:carrier.example.net:", "203.0.113.10", "203.0.113.10")]
    [InlineData("sip:carrier.example.net:50x0", "203.0.113.10", "203.0.113.10")]
    [InlineData("sip:carrier example.net", "203.0.113.10", "203.0.113.10")]
    public void From_ShouldAnswerTheDialHost_WhenGivenTheTrunksAddresses(string? registrationUri, string? matchHost, string? expected)
    {
        var trunk = new Trunk { Id = 1, Name = "pstn", RegistrationUri = registrationUri, MatchHost = matchHost };

        DialHost.From(trunk).Should().Be(expected);
    }

    [Fact]
    public void From_ShouldAnswerNull_WhenTheTrunkHasOnlyAuthCredentials()
    {
        var trunk = new Trunk { Id = 4, Name = "auth-only", AuthUsername = "user", AuthPassword = "secret" };

        DialHost.From(trunk).Should().BeNull("an auth-only trunk names no address Asterisk could send an INVITE to");
    }
}
