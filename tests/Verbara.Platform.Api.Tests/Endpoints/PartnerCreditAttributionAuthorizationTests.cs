using System.Net;
using System.Net.Http.Headers;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Verbara.Platform.Api.Tests.Endpoints;

/// <summary>
/// GET /partner/credit-ledger/attribution lists the partner's customers with the credits each one
/// drew from partner-funded grants: billing data, readable with <c>partner:billing:view</c> like the
/// rest of the partner billing surface, not by every user of the Partner tenant.
/// </summary>
public sealed class PartnerCreditAttributionAuthorizationTests : IDisposable
{
    private const string Route = "/api/v1/partner/credit-ledger/attribution";
    private const string BillingUserId = "partner-billing-user";
    private const string AgentUserId = "partner-agent-user";

    private readonly PartnerApiFactory _root = new();
    private readonly WebApplicationFactory<Program> _factory;

    public PartnerCreditAttributionAuthorizationTests()
    {
        // Only the billing user holds partner:billing:view; the agent holds no permission at all.
        var roles = Substitute.For<IUserRoleStore>();
        roles.GetEffectivePermissionsAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<IReadOnlySet<string>>(
                ci.ArgAt<EntityId>(1).Value == BillingUserId
                    ? new HashSet<string>(StringComparer.Ordinal) { "partner:billing:view" }
                    : new HashSet<string>(StringComparer.Ordinal)));

        _factory = _root.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            foreach (var d in services.Where(d => d.ServiceType == typeof(IUserRoleStore)).ToList())
                services.Remove(d);
            services.AddSingleton(roles);
        }));
    }

    public void Dispose()
    {
        _factory.Dispose();
        _root.Dispose();
    }

    [Fact]
    public async Task GetPartnerAttribution_ShouldReturn403_WhenTheCallerHoldsNoPartnerBillingPermission()
    {
        using var client = BearerClientFor(AgentUserId);

        using var response = await client.GetAsync(Route);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetPartnerAttribution_ShouldReturn200_WhenTheCallerHoldsPartnerBillingView()
    {
        using var client = BearerClientFor(BillingUserId);

        using var response = await client.GetAsync(Route);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetPartnerAttribution_ShouldReturn200_WhenThePartnerAdministratorsKeyCallsIt()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {PartnerApiFactory.TestPartnerApiKey}");
        client.DefaultRequestHeaders.Add("X-Tenant-Id", PartnerApiFactory.PartnerTenantId);

        using var response = await client.GetAsync(Route);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>A client carrying an access token the host issues for a non-admin user of the Partner tenant.</summary>
    private HttpClient BearerClientFor(string userId)
    {
        var user = new User
        {
            UserId = EntityId.From(userId),
            TenantId = new TenantId(PartnerApiFactory.PartnerTenantId),
            Email = $"{userId}@partner.test",
            DisplayName = userId,
            Role = UserRole.Agent,
            Status = UserStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var (token, _) = _factory.Services.GetRequiredService<JwtTokenService>().GenerateAccessToken(user);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
