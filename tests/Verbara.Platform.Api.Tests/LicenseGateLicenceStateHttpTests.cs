using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Verbara.Sdk.Pro.Licensing;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// End-to-end check of the HTTP licence gate over the real Pro licence stack
/// (<see cref="LicenseStatusTracker"/> + <see cref="LicenseGuard"/>), with a licence that
/// carries every feature bit but whose validation result is not <c>Valid</c>.
/// PDR-0003 §E: <c>/admin/skills</c> (Routing) and <c>/admin/realtime/profiles</c>
/// (Realtime) never answer 402 once a licence expires; the dialer does.
/// </summary>
public sealed class LicenseGateLicenceStateHttpTests
{
    private const string SkillsPath = "/api/v1/admin/skills";
    // The profile store is a stub in this host: a lookup by id answers 404 once the gate lets it in.
    private const string RealtimeProfilesPath = "/api/v1/admin/realtime/profiles/1";
    private const string CampaignsPath = "/api/v1/admin/campaigns";

    private static async Task<HttpStatusCode> GetAsync(LicenceStateFactory factory, string path)
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {AuthenticatedPlatformApiFactory.TestApiKey}");
        client.DefaultRequestHeaders.Add("X-Tenant-Id", AuthenticatedPlatformApiFactory.TestTenantId);
        using var response = await client.GetAsync(path);
        return response.StatusCode;
    }

    [Theory]
    [InlineData(SkillsPath, HttpStatusCode.OK)]
    [InlineData(RealtimeProfilesPath, HttpStatusCode.NotFound)]
    [InlineData(CampaignsPath, HttpStatusCode.OK)]
    public async Task Get_ShouldReachTheHandler_WhenLicenceIsValid(string path, HttpStatusCode expected)
    {
        using var factory = new LicenceStateFactory(LicenseValidationResult.Valid);

        (await GetAsync(factory, path)).Should().Be(expected);
    }

    [Theory]
    [InlineData(SkillsPath, HttpStatusCode.OK)]
    [InlineData(RealtimeProfilesPath, HttpStatusCode.NotFound)]
    public async Task Get_ShouldReachTheHandler_WhenLicenceExpiredForRoutingAndRealtime(string path, HttpStatusCode expected)
    {
        using var factory = new LicenceStateFactory(LicenseValidationResult.Expired);

        (await GetAsync(factory, path)).Should().Be(expected);
    }

    [Fact]
    public async Task Get_ShouldReturn402_WhenLicenceExpiredForTheDialer()
    {
        using var factory = new LicenceStateFactory(LicenseValidationResult.Expired);

        (await GetAsync(factory, CampaignsPath)).Should().Be(HttpStatusCode.PaymentRequired);
    }

    [Theory]
    [InlineData(SkillsPath)]
    [InlineData(RealtimeProfilesPath)]
    [InlineData(CampaignsPath)]
    public async Task Get_ShouldReturn402_WhenSignatureIsInvalid(string path)
    {
        using var factory = new LicenceStateFactory(LicenseValidationResult.Invalid);

        (await GetAsync(factory, path)).Should().Be(HttpStatusCode.PaymentRequired);
    }

    /// <summary>
    /// Admin-authenticated host whose <see cref="ILicenseStatus"/> is the real tracker the
    /// real guard reads, set to <paramref name="result"/> with a key carrying every bit.
    /// The licence hosted services are removed by <c>StubVerbaraHostedServices</c>, so
    /// nothing overwrites the state after start.
    /// </summary>
    private sealed class LicenceStateFactory(LicenseValidationResult result) : WebApplicationFactory<Program>
    {
        private static readonly string s_hashedKey =
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(AuthenticatedPlatformApiFactory.TestApiKey)));

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.UseEnvironment("Testing");

            builder.ConfigureServices(services =>
            {
                AuthenticatedPlatformApiFactory.SetupTestAuth(
                    services, s_hashedKey,
                    AuthenticatedPlatformApiFactory.TestTenantId,
                    AuthenticatedPlatformApiFactory.TestUserId);
                AuthenticatedPlatformApiFactory.StubVerbaraHostedServices(services);

                foreach (var d in services.Where(d => d.ServiceType == typeof(ILicenseStatus)).ToList())
                    services.Remove(d);
                services.AddSingleton<ILicenseStatus>(sp => sp.GetRequiredService<LicenseStatusTracker>());
                if (!services.Any(d => d.ServiceType == typeof(byte[])))
                    services.AddSingleton<byte[]>([]);

                AuthenticatedPlatformApiFactory.RegisterInMemoryStores(services);
            });

            var host = base.CreateHost(builder);

            AuthenticatedPlatformApiFactory.SeedEnterpriseFeatureGate(host.Services, AuthenticatedPlatformApiFactory.TestTenantId);
            AuthenticatedPlatformApiFactory.SeedTestCustomerTenant(host.Services, AuthenticatedPlatformApiFactory.TestTenantId);

            var now = DateTimeOffset.UtcNow;
            host.Services.GetRequiredService<LicenseStatusTracker>().Update(
                result,
                new LicenseKey("lic-http-test", "Test Licensee", LicenseTier.SelfHostBusiness,
                    result == LicenseValidationResult.Valid ? now.AddDays(30) : now.AddDays(-30),
                    LicenseFeature.All, MaxAgents: 50, MaxNodes: 3, Signature: "not-checked-by-the-gate"));

            return host;
        }
    }
}
