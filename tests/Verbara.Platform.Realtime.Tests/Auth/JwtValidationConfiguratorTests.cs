using Verbara.Platform.Identity.Auth;
using Verbara.Platform.Realtime.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Realtime.Tests.Auth;

public sealed class JwtValidationConfiguratorTests
{
    [Fact]
    public void BuildValidationParameters_ShouldGrantNoMoreClockSkewThanARevocationOutlivesExpiry()
    {
        // A revoked impersonation token is denylisted until its exp + MaxClockSkew; a larger skew here
        // would let Realtime admit it again after the revocation was dropped.
        using var services = new ServiceCollection().BuildServiceProvider();

        var parameters = JwtValidationConfigurator.BuildValidationParameters(services);

        parameters.ClockSkew.Should().BeLessThanOrEqualTo(ImpersonationTokenRevocation.MaxClockSkew);
    }
}
