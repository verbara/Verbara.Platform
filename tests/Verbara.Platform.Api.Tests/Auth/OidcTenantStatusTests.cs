using System.Net;
using Verbara.Platform.Api.Endpoints;
using Verbara.Sdk.Pro.MultiTenant;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Api.Tests.Auth;

/// <summary>
/// The OIDC callback holds the tenant of its flow state to the same status rule as every other sign-in path:
/// a tenant that is not serving gets the answer an account that may not authenticate gets (401, no tokens, no
/// MFA challenge). These tests run the real host pipeline; the callback request names no tenant in a header
/// or subdomain.
/// </summary>
public sealed class OidcTenantStatusTests
{
    [Theory]
    [InlineData(TenantStatus.Suspended, false)]
    [InlineData(TenantStatus.PendingDeletion, false)]
    [InlineData(TenantStatus.Suspended, true)]
    public async Task OidcCallback_ShouldAnswer401WithoutTokens_WhenFlowTenantIsNotServing(TenantStatus status, bool mfaEnabled)
    {
        using var factory = new OidcSignInApiFactory(mfaEnabled: mfaEnabled);
        UpsertTenant(factory, status);

        using var response = await factory.CallBackResponseAsync("/login");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            because: "a tenant that is not serving gets the same outcome as an account that may not authenticate");
        response.Headers.Location.Should().BeNull(because: "neither tokens nor an MFA challenge are handed back");
        var cookies = response.Headers.TryGetValues("Set-Cookie", out var values) ? values : [];
        cookies.Should().NotContain(c => c.StartsWith(RefreshTokenCookie.Name + "=", StringComparison.Ordinal)
            && !c.StartsWith(RefreshTokenCookie.Name + "=;", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(TenantStatus.Active)]
    [InlineData(TenantStatus.Warning)]
    public async Task OidcCallback_ShouldSendTheAccessToken_WhenFlowTenantIsServing(TenantStatus status)
    {
        using var factory = new OidcSignInApiFactory();
        UpsertTenant(factory, status);

        using var response = await factory.CallBackResponseAsync("/login");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect, await response.Content.ReadAsStringAsync());
        response.Headers.Location!.OriginalString.Should().StartWith("/login#oidc_callback&access_token=");
    }

    private static void UpsertTenant(OidcSignInApiFactory factory, TenantStatus status)
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantStore>().UpsertAsync(new Tenant
        {
            TenantId = OidcSignInApiFactory.TenantId,
            Name = OidcSignInApiFactory.TenantId,
            Status = status,
            Type = TenantType.Customer,
        }).AsTask().GetAwaiter().GetResult();
    }
}
