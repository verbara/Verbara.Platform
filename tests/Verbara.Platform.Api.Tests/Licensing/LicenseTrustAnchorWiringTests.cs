using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Verbara.Sdk.Pro.Licensing;

namespace Verbara.Platform.Api.Tests.Licensing;

/// <summary>
/// The licence trust anchor of the fully built Api host is the key embedded in Pro,
/// <see cref="LicenseTrustAnchor.OfficialPublicKey"/>, whatever the configuration says.
/// The host registers no <c>byte[]</c> of its own: <c>Licensing:PublicKeyPath</c> is not read.
/// </summary>
public sealed class LicenseTrustAnchorWiringTests : IDisposable
{
    private const string CampaignsPath = "/api/v1/admin/campaigns";

    private readonly string _dir = Directory.CreateTempSubdirectory("trust-anchor-test-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Resolved_ShouldBeOfficialPublicKey_WhenPublicKeyPathUnset()
    {
        using var factory = new TrustAnchorFactory(new Dictionary<string, string>());

        var resolved = factory.Services.GetRequiredService<byte[]>();

        resolved.Should().Equal(LicenseTrustAnchor.OfficialPublicKey,
            "with no configuration the host must trust the embedded key");
    }

    [Fact]
    public void Resolved_ShouldBeOfficialPublicKey_WhenPublicKeyPathPointsToAnotherValidKey()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var otherKeyPath = WriteFile("other.spki", other.ExportSubjectPublicKeyInfo());

        using var factory = new TrustAnchorFactory(new Dictionary<string, string>
        {
            ["Licensing:PublicKeyPath"] = otherKeyPath,
        });

        var resolved = factory.Services.GetRequiredService<byte[]>();

        resolved.Should().Equal(LicenseTrustAnchor.OfficialPublicKey,
            "Licensing:PublicKeyPath is not read; the embedded key stays the only trust anchor");
    }

    [Fact]
    public async Task Get_ShouldReturn402_WhenLicenceIsSignedByAnotherKey()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var otherPublicKey = other.ExportSubjectPublicKeyInfo();
        var licence = SignLicence(other);
        // The licence is genuinely signed: against the other key it validates.
        LicenseValidator.Validate(licence, otherPublicKey, TimeSpan.FromDays(7))
            .Should().Be(LicenseValidationResult.Valid);

        var licencePath = WriteFile("license.lic",
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(licence, TrustAnchorTestJsonContext.Default.LicenseKey)));
        var otherKeyPath = WriteFile("other.spki", otherPublicKey);

        using var factory = new TrustAnchorFactory(new Dictionary<string, string>
        {
            ["Licensing:FilePath"] = licencePath,
            ["Licensing:PublicKeyPath"] = otherKeyPath,
        }, validateLicence: true);

        factory.Services.GetRequiredService<LicenseStatusTracker>().LastResult
            .Should().Be(LicenseValidationResult.Invalid);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {AuthenticatedPlatformApiFactory.TestApiKey}");
        client.DefaultRequestHeaders.Add("X-Tenant-Id", AuthenticatedPlatformApiFactory.TestTenantId);
        using var response = await client.GetAsync(CampaignsPath);

        response.StatusCode.Should().Be(HttpStatusCode.PaymentRequired);
    }

    private string WriteFile(string name, byte[] content)
    {
        var path = Path.Join(_dir, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static LicenseKey SignLicence(ECDsa privateKey)
    {
        var payload = new TrustAnchorTestPayload(
            "lic-trust-anchor-test",
            "Test Licensee",
            LicenseTier.SelfHostBusiness,
            new DateTimeOffset(DateTime.UtcNow.Date.AddDays(30), TimeSpan.Zero),
            FeatureRegistry.GetCanonicalFeaturesForTier(LicenseTier.SelfHostBusiness),
            MaxAgents: 50,
            MaxNodes: 3);
        var data = Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(payload, TrustAnchorTestJsonContext.Default.TrustAnchorTestPayload));
        var signature = Convert.ToBase64String(privateKey.SignData(data, HashAlgorithmName.SHA256));
        return new LicenseKey(payload.LicenseId, payload.Licensee, payload.Tier, payload.ExpiresAt,
            payload.Features, payload.MaxAgents, payload.MaxNodes, signature);
    }

    /// <summary>
    /// Admin-authenticated host over the real Pro licence stack. With
    /// <paramref name="validateLicence"/> the licence validation hosted service runs at start,
    /// against whatever <c>byte[]</c> the host resolves.
    /// </summary>
    private sealed class TrustAnchorFactory(
        IReadOnlyDictionary<string, string> settings,
        bool validateLicence = false) : WebApplicationFactory<Program>
    {
        private static readonly string s_hashedKey =
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(AuthenticatedPlatformApiFactory.TestApiKey)));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            foreach (var (key, value) in settings)
                builder.UseSetting(key, value);
        }

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
                if (validateLicence)
                    services.AddSingleton<IHostedService, LicenseValidationHostedService>();

                foreach (var d in services.Where(d => d.ServiceType == typeof(ILicenseStatus)).ToList())
                    services.Remove(d);
                services.AddSingleton<ILicenseStatus>(sp => sp.GetRequiredService<LicenseStatusTracker>());

                AuthenticatedPlatformApiFactory.RegisterInMemoryStores(services);
            });

            var host = base.CreateHost(builder);

            AuthenticatedPlatformApiFactory.SeedEnterpriseFeatureGate(host.Services, AuthenticatedPlatformApiFactory.TestTenantId);
            AuthenticatedPlatformApiFactory.SeedTestCustomerTenant(host.Services, AuthenticatedPlatformApiFactory.TestTenantId);

            return host;
        }
    }
}

/// <summary>The signed part of a licence, in the field order Pro signs it.</summary>
internal sealed record TrustAnchorTestPayload(
    string LicenseId,
    string Licensee,
    LicenseTier Tier,
    DateTimeOffset ExpiresAt,
    LicenseFeature Features,
    int MaxAgents,
    int MaxNodes);

[JsonSerializable(typeof(TrustAnchorTestPayload))]
[JsonSerializable(typeof(LicenseKey))]
internal sealed partial class TrustAnchorTestJsonContext : JsonSerializerContext;
