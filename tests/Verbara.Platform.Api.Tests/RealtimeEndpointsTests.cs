using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Verbara.Sdk.Pro.Realtime;
using Verbara.Sdk.Pro.Realtime.Models;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// Integration tests for the realtime endpoint-profile admin surface
/// (<c>/api/v1/admin/realtime/profiles</c>). Uses the pre-seeded Enterprise
/// authenticated client so <c>AdminOnly</c>, <c>RequireOperationalTenant</c>,
/// and <c>RequireLicenseFeature(Realtime)</c> filters all pass.
/// </summary>
public sealed class RealtimeEndpointsTests : IClassFixture<AuthenticatedPlatformApiFactory>
{
    private readonly HttpClient _admin;

    public RealtimeEndpointsTests(AuthenticatedPlatformApiFactory adminFactory)
    {
        _admin = adminFactory.CreateAuthenticatedClient();
    }

    [Fact]
    public async Task CreateProfile_ShouldReturn400_WhenCodecTokenInvalid()
    {
        var body = new
        {
            name = "profile-badcodec",
            type = "agent",
            transport = (string?)null,
            codecs = "opus,ulwa",
            webrtc = (bool?)null,
            maxContacts = (int?)null,
            directMedia = (bool?)null,
            context = (string?)null,
            qualifyFrequency = (int?)null,
        };

        var response = await _admin.PostAsJsonAsync("/api/v1/admin/realtime/profiles", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var invalidCodecs = json["invalidCodecs"]!.AsArray()
            .Select(n => n!.GetValue<string>())
            .ToArray();
        invalidCodecs.Should().ContainSingle().Which.Should().Be("ulwa");
    }
}

/// <summary>
/// Integration tests for the PUT /admin/realtime/profiles/{id} endpoint that require
/// a real in-memory profile store (so POST→PUT round-trips work without a database).
/// Uses a dedicated factory that replaces the NSubstitute stub with
/// <see cref="InMemoryEndpointProfileStore"/>.
/// </summary>
public sealed class RealtimeEndpointsUpdateTests : IClassFixture<RealtimeEndpointsUpdateTests.Factory>
{
    private readonly HttpClient _admin;

    public RealtimeEndpointsUpdateTests(Factory factory)
    {
        _admin = factory.CreateAuthenticatedClient();
    }

    [Fact]
    public async Task UpdateProfile_ShouldReturn400_WhenCodecTokenInvalid()
    {
        // Arrange — create a valid profile first to obtain its generated id
        var createBody = new
        {
            name = "profile-update-badcodec",
            type = "agent",
            transport = (string?)null,
            codecs = "ulaw,alaw",
            webrtc = (bool?)null,
            maxContacts = (int?)null,
            directMedia = (bool?)null,
            context = (string?)null,
            qualifyFrequency = (int?)null,
        };

        var createResponse = await _admin.PostAsJsonAsync("/api/v1/admin/realtime/profiles", createBody);
        createResponse.StatusCode.Should().BeOneOf(HttpStatusCode.Created, HttpStatusCode.OK);
        var createJson = JsonNode.Parse(await createResponse.Content.ReadAsStringAsync())!;
        var id = createJson["id"]!.GetValue<long>();

        // Act — PUT with a single invalid codec token to trigger the validation branch
        var updateBody = new { codecs = "opus,ulwa" };
        var response = await _admin.PutAsJsonAsync($"/api/v1/admin/realtime/profiles/{id}", updateBody);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var invalidCodecs = json["invalidCodecs"]!.AsArray()
            .Select(n => n!.GetValue<string>())
            .ToArray();
        invalidCodecs.Should().Contain("ulwa");
    }

    // ─── Factory ──────────────────────────────────────────────────────────────

    /// <summary>
    /// WebApplicationFactory that replicates the <see cref="AuthenticatedPlatformApiFactory"/>
    /// setup but wires a real <see cref="InMemoryEndpointProfileStore"/> so POST→PUT
    /// round-trips resolve correctly without a database.
    /// </summary>
    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.UseEnvironment("Testing");

            builder.ConfigureServices(services =>
            {
                AuthenticatedPlatformApiFactory.SetupTestAuth(
                    services,
                    HashKey(AuthenticatedPlatformApiFactory.TestApiKey),
                    AuthenticatedPlatformApiFactory.TestTenantId,
                    AuthenticatedPlatformApiFactory.TestUserId);

                AuthenticatedPlatformApiFactory.StubVerbaraHostedServices(services);
                services.AddAllProFeaturesLicensed();
                if (!services.Any(d => d.ServiceType == typeof(byte[])))
                    services.AddSingleton<byte[]>([]);

                AuthenticatedPlatformApiFactory.RegisterInMemoryStores(services);

                // Replace the NSubstitute stub set by RegisterInMemoryStores with a real
                // in-memory store so CreateAsync returns a usable id and GetAsync finds it.
                var descriptors = services.Where(d => d.ServiceType == typeof(EndpointProfileStoreBase)).ToList();
                foreach (var d in descriptors) services.Remove(d);
                services.AddSingleton<EndpointProfileStoreBase, InMemoryEndpointProfileStore>();
            });

            var host = base.CreateHost(builder);

            AuthenticatedPlatformApiFactory.SeedEnterpriseFeatureGate(
                host.Services, AuthenticatedPlatformApiFactory.TestTenantId);
            AuthenticatedPlatformApiFactory.SeedTestCustomerTenant(
                host.Services, AuthenticatedPlatformApiFactory.TestTenantId);

            return host;
        }

        public HttpClient CreateAuthenticatedClient()
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.Add("Authorization",
                $"Bearer {AuthenticatedPlatformApiFactory.TestApiKey}");
            client.DefaultRequestHeaders.Add("X-Tenant-Id",
                AuthenticatedPlatformApiFactory.TestTenantId);
            return client;
        }

        private static string HashKey(string rawKey)
        {
            var bytes = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(rawKey));
            return Convert.ToHexStringLower(bytes);
        }
    }

    // ─── In-memory store ─────────────────────────────────────────────────────

    private sealed class InMemoryEndpointProfileStore : EndpointProfileStoreBase
    {
        private readonly ConcurrentDictionary<long, EndpointProfile> _store = new();
        private long _nextId;

        public override ValueTask<EndpointProfile?> GetAsync(long id, string tenantId, CancellationToken ct = default)
        {
            _store.TryGetValue(id, out var profile);
            return ValueTask.FromResult(profile?.TenantId == tenantId ? profile : null);
        }

        public override ValueTask<EndpointProfile?> GetDefaultAsync(string tenantId, EndpointProfileType type, CancellationToken ct = default)
            => ValueTask.FromResult<EndpointProfile?>(null);

        public override ValueTask<IReadOnlyList<EndpointProfile>> ListAsync(string tenantId, CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<EndpointProfile>>([]);

        public override ValueTask<long> CreateAsync(EndpointProfile profile, string tenantId, CancellationToken ct = default)
        {
            var id = Interlocked.Increment(ref _nextId);
            profile.Id = id;
            profile.TenantId = tenantId;
            _store[id] = profile;
            return ValueTask.FromResult(id);
        }

        public override ValueTask UpdateAsync(EndpointProfile profile, string tenantId, CancellationToken ct = default)
        {
            _store[profile.Id] = profile;
            return ValueTask.CompletedTask;
        }

        public override ValueTask DeleteAsync(long id, string tenantId, CancellationToken ct = default)
        {
            _store.TryRemove(id, out _);
            return ValueTask.CompletedTask;
        }

        public override ValueTask SeedDefaultsAsync(string tenantId, CancellationToken ct = default)
            => ValueTask.CompletedTask;
    }
}
