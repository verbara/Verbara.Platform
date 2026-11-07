using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json.Nodes;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Identity.Auth.Jwt;
using Verbara.Sdk.Pro.EventStore;
using Verbara.Sdk.Pro.MultiTenant;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Verbara.Platform.Api.Tests.Recordings;

/// <summary>
/// The console plays a recording by fetching <c>GET /api/v1/recordings/{sessionId}/stream</c> through its
/// authenticated client, which sends <c>Authorization</c> and <c>X-Tenant-Id</c> as on every request
/// (call-records-are-written-once D19). The stream path needs no change for that; these tests pin what the
/// player relies on: the header names the tenant the file is served from, the tenant boundary still refuses a
/// tenant the caller may not act in, a missing or expired token is a 401 the client can recover from, and the
/// record's session id (32 lowercase hex) is the route segment as it is, unencoded.
/// </summary>
/// <remarks>
/// The fixture runs the JWT rotation-pool path so the expired-token case can sign a token with the host's own
/// active key; every other case is independent of which key path signs.
/// </remarks>
public sealed class RecordingStreamEndpointTests : IClassFixture<RecordingStreamEndpointTests.RecordingStreamApiFactory>
{
    private readonly RecordingStreamApiFactory _factory;

    public RecordingStreamEndpointTests(RecordingStreamApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task StreamRecording_ShouldServeTheCustomersFile_WhenAPartnerAdminNamesItsCustomerInXTenantId()
    {
        using var client = _factory.CreateClient();
        using var request = StreamRequest(RecordingStreamApiFactory.CustomerSessionId, _factory.MintPartnerAdminJwt());
        request.Headers.Add("X-Tenant-Id", RecordingStreamApiFactory.PartnerCustomerTenantId);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            because: "the partner admin's tid is its own tenant and the header names a customer in its subtree");
        response.Content.Headers.ContentType!.MediaType.Should().Be("audio/wav");
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(RecordingStreamApiFactory.CustomerFileBytes,
            because: "the file is read from the tenant the header names, not from the caller's own tenant");
    }

    [Fact]
    public async Task StreamRecording_ShouldReturn403_WhenXTenantIdNamesATenantTheCallerMayNotActIn()
    {
        using var client = _factory.CreateClient();
        using var request = StreamRequest(RecordingStreamApiFactory.RivalSessionId, _factory.MintPartnerAdminJwt());
        request.Headers.Add("X-Tenant-Id", RecordingStreamApiFactory.RivalCustomerTenantId);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            because: "the rival partner's customer is outside the caller's subtree, and that tenant has a recorded call");
        (await response.Content.ReadAsStringAsync()).Should().Contain("does not match authenticated principal",
            because: "the refusal is the tenant boundary's, not a plan or role gate's");
    }

    [Fact]
    public async Task StreamRecording_ShouldReturn401_WhenTheRequestCarriesNoToken()
    {
        using var client = _factory.CreateClient();
        using var request = StreamRequest(RecordingStreamApiFactory.CustomerSessionId, bearer: null);
        request.Headers.Add("X-Tenant-Id", RecordingStreamApiFactory.PartnerCustomerTenantId);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task StreamRecording_ShouldReturn401_WhenTheAccessTokenHasExpired()
    {
        using var client = _factory.CreateClient();
        var now = DateTime.UtcNow;

        // Control: the same hand-minted token with a lifetime that has not ended is served, so the 401 below
        // is the expiry's and not the minting's.
        using var control = StreamRequest(RecordingStreamApiFactory.CustomerSessionId,
            await _factory.MintPoolSignedPartnerAdminJwtAsync(issuedAt: now.AddMinutes(-1), expires: now.AddMinutes(10)));
        control.Headers.Add("X-Tenant-Id", RecordingStreamApiFactory.PartnerCustomerTenantId);
        using var controlResponse = await client.SendAsync(control);
        controlResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // exp five minutes ago: well past JwtBearer's 30-second clock skew.
        using var request = StreamRequest(RecordingStreamApiFactory.CustomerSessionId,
            await _factory.MintPoolSignedPartnerAdminJwtAsync(issuedAt: now.AddMinutes(-20), expires: now.AddMinutes(-5)));
        request.Headers.Add("X-Tenant-Id", RecordingStreamApiFactory.PartnerCustomerTenantId);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            because: "the console's client refreshes and retries on a 401, which an expired token must produce");
    }

    [Fact]
    public async Task StreamRecording_ShouldResolveTheRecordsSessionId_WhenItIsTheRouteSegmentUnencoded()
    {
        var sessionId = RecordingStreamApiFactory.CustomerSessionId;
        sessionId.Should().MatchRegex("^[0-9a-f]{32}$");
        Uri.EscapeDataString(sessionId).Should().Be(sessionId, because: "32 hex needs no encoding in a path segment");

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _factory.MintPartnerAdminJwt());
        client.DefaultRequestHeaders.Add("X-Tenant-Id", RecordingStreamApiFactory.PartnerCustomerTenantId);

        using var response = await client.GetAsync($"/api/v1/recordings/{sessionId}/stream");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(RecordingStreamApiFactory.CustomerFileBytes);
    }

    // ─── The links to the stream (call-records-are-written-once D11) ─────────

    [Fact]
    public async Task GetCdrDetail_ShouldLinkTheVersionedStreamRoute_WhenTheCallIsRecorded()
    {
        using var client = PartnerAdminClientForCustomer();
        var sessionId = RecordingStreamApiFactory.CustomerSessionId;

        var detail = await client.GetFromJsonAsync<JsonObject>($"/api/v1/analytics/cdr/{sessionId}");

        var expected = $"/api/v1/recordings/{sessionId}/stream";
        var streamUrl = detail!["recordingStreamUrl"]?.GetValue<string>();
        streamUrl.Should().Be(expected,
            because: "the Api maps the stream under /api/v{version} only, so the detail links that route");
        await AssertTheLinkServesTheCustomersFile(client, expected);
    }

    [Fact]
    public async Task GetRecordingMetadata_ShouldLinkTheVersionedStreamRoute_WhenTheCallIsRecorded()
    {
        using var client = PartnerAdminClientForCustomer();
        var sessionId = RecordingStreamApiFactory.CustomerSessionId;

        var metadata = await client.GetFromJsonAsync<JsonObject>($"/api/v1/recordings/{sessionId}");

        var expected = $"/api/v1/recordings/{sessionId}/stream";
        var streamUrl = metadata!["streamUrl"]?.GetValue<string>();
        streamUrl.Should().Be(expected,
            because: "the Api maps the stream under /api/v{version} only, so the metadata links that route");
        await AssertTheLinkServesTheCustomersFile(client, expected);
    }

    private HttpClient PartnerAdminClientForCustomer()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _factory.MintPartnerAdminJwt());
        client.DefaultRequestHeaders.Add("X-Tenant-Id", RecordingStreamApiFactory.PartnerCustomerTenantId);
        return client;
    }

    private static async Task AssertTheLinkServesTheCustomersFile(HttpClient client, string streamUrl)
    {
        // The emitted path, requested as it is: it reaches StreamRecording (the file's bytes), not an unmapped 404.
        using var response = await client.GetAsync(streamUrl);
        response.StatusCode.Should().Be(HttpStatusCode.OK, because: "the link names a mapped route");
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(RecordingStreamApiFactory.CustomerFileBytes,
            because: "the route is the stream handler's, which serves the recorded call's file");
    }

    private static HttpRequestMessage StreamRequest(string sessionId, string? bearer)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/recordings/{sessionId}/stream");
        if (bearer is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return request;
    }

    /// <summary>
    /// Platform host → two partners, each with one customer. Each customer has one recorded call whose file
    /// sits under <c>{BasePath}/{tenantId}/</c>; the partner's own directory holds a different file under the
    /// same name, so serving from the wrong tenant is visible in the bytes. Every tenant is on the Enterprise plan.
    /// </summary>
    public sealed class RecordingStreamApiFactory : WebApplicationFactory<Program>
    {
        public const string PlatformTenantId = "rec-platform";
        public const string PartnerTenantId = "rec-partner";
        public const string PartnerCustomerTenantId = "rec-partner-customer";
        public const string RivalPartnerTenantId = "rec-rival-partner";
        public const string RivalCustomerTenantId = "rec-rival-customer";
        public const string PartnerAdminUserId = "rec-partner-admin";
        private const string PartnerAdminEmail = "rec-partner-admin@test.internal";
        private const string RecordingName = "call-recording";

        public static readonly string CustomerSessionId = Guid.NewGuid().ToString("N");
        public static readonly string RivalSessionId = Guid.NewGuid().ToString("N");
        public static readonly byte[] CustomerFileBytes = "RIFF-customer-recording"u8.ToArray();
        private static readonly byte[] PartnerFileBytes = "RIFF-partner-recording"u8.ToArray();
        private static readonly byte[] RivalFileBytes = "RIFF-rival-recording"u8.ToArray();

        private readonly string _root = Path.Join(Path.GetTempPath(), $"rec-stream-{Guid.NewGuid():N}");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Recordings:BasePath", Path.Join(_root, "recordings"));
            builder.UseSetting("Auth:KeyDirectory", Path.Join(_root, "keys"));
            builder.UseSetting("Identity:JwtKeyRotation:UseRotationPool", "true");
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                AuthenticatedPlatformApiFactory.StubVerbaraHostedServices(services);
                AuthenticatedPlatformApiFactory.RegisterInMemoryStores(services);
                services.AddAllProFeaturesLicensed();
                if (!services.Any(d => d.ServiceType == typeof(byte[])))
                    services.AddSingleton<byte[]>([]);
            });

            var host = base.CreateHost(builder);
            Seed(host.Services);
            return host;
        }

        public string MintPartnerAdminJwt()
        {
            using var scope = Services.CreateScope();
            var (token, _) = scope.ServiceProvider.GetRequiredService<JwtTokenService>().GenerateAccessToken(PartnerAdmin());
            return token;
        }

        /// <summary>
        /// A partner-admin token signed with the host's active pool key, with the lifetime the caller chooses.
        /// </summary>
        public async Task<string> MintPoolSignedPartnerAdminJwtAsync(DateTime issuedAt, DateTime expires)
        {
            var entry = await Services.GetRequiredService<IJwtKeyRotationService>().GetActiveSigningKeyAsync();
            entry.Algorithm.Should().Be(JwtKeyAlgorithm.Hs256, because: "the pool bootstraps an HS256 key");
            var key = new SymmetricSecurityKey(Convert.FromBase64String(entry.Key)) { KeyId = entry.KeyId };
            return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = "verbara-platform",
                Audience = "verbara-platform",
                Subject = new ClaimsIdentity(
                [
                    new Claim("sub", PartnerAdminUserId),
                    new Claim("tid", PartnerTenantId),
                    new Claim("role", nameof(UserRole.Admin)),
                    new Claim("jti", Guid.NewGuid().ToString()),
                ]),
                IssuedAt = issuedAt,
                NotBefore = issuedAt,
                Expires = expires,
                SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }

        private void Seed(IServiceProvider services)
        {
            var recordings = Path.Join(_root, "recordings");
            WriteRecording(recordings, PartnerCustomerTenantId, CustomerFileBytes);
            WriteRecording(recordings, PartnerTenantId, PartnerFileBytes);
            WriteRecording(recordings, RivalCustomerTenantId, RivalFileBytes);

            using var scope = services.CreateScope();
            var tenants = scope.ServiceProvider.GetRequiredService<ITenantStore>();
            UpsertTenant(tenants, PlatformTenantId, TenantType.Platform, parent: null);
            UpsertTenant(tenants, PartnerTenantId, TenantType.Partner, parent: PlatformTenantId);
            UpsertTenant(tenants, PartnerCustomerTenantId, TenantType.Customer, parent: PartnerTenantId);
            UpsertTenant(tenants, RivalPartnerTenantId, TenantType.Partner, parent: PlatformTenantId);
            UpsertTenant(tenants, RivalCustomerTenantId, TenantType.Customer, parent: RivalPartnerTenantId);

            scope.ServiceProvider.GetRequiredService<IUserStore>()
                .CreateAsync(PartnerAdmin(), CancellationToken.None).GetAwaiter().GetResult();

            var cdr = scope.ServiceProvider.GetRequiredService<ICompletedSessionStore>();
            cdr.UpsertAsync(RecordedCall(PartnerCustomerTenantId, CustomerSessionId)).AsTask().GetAwaiter().GetResult();
            cdr.UpsertAsync(RecordedCall(RivalCustomerTenantId, RivalSessionId)).AsTask().GetAwaiter().GetResult();
        }

        private static void WriteRecording(string basePath, string tenantId, byte[] bytes)
        {
            var dir = Path.Join(basePath, tenantId);
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Join(dir, RecordingName + ".wav"), bytes);
        }

        private static void UpsertTenant(ITenantStore store, string tenantId, TenantType type, string? parent)
        {
            store.UpsertAsync(new Tenant
            {
                TenantId = tenantId,
                Name = tenantId,
                Status = TenantStatus.Active,
                Type = type,
                ParentTenantId = parent,
                // TenantStatusMiddleware rebuilds the feature-gate entry from the tenant's plan (capped by its
                // parent's) on every request, so the plan that carries Recordings is set on the tenant itself.
                Metadata = new() { ["Plan"] = nameof(TenantPlan.Enterprise) },
            }).AsTask().GetAwaiter().GetResult();
        }

        private static User PartnerAdmin() => new()
        {
            UserId = EntityId.From(PartnerAdminUserId),
            TenantId = new TenantId(PartnerTenantId),
            Email = PartnerAdminEmail,
            DisplayName = PartnerAdminEmail,
            Role = UserRole.Admin,
            Status = UserStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        private static CompletedSessionRow RecordedCall(string tenantId, string sessionId)
        {
            var now = DateTimeOffset.UtcNow;
            return new CompletedSessionRow
            {
                TenantId = tenantId,
                SessionId = sessionId,
                ServerId = "server-1",
                Direction = 0,
                CallerIdNum = "+15551234567",
                StartedAt = now.AddHours(-1),
                ConnectedAt = now.AddHours(-1).AddSeconds(10),
                CompletedAt = now.AddHours(-1).AddMinutes(5),
                DurationMs = 300_000,
                TalkTimeMs = 280_000,
                WaitTimeMs = 10_000,
                HoldTimeMs = 0,
                HangupCause = 16,
                FinalState = 8,
                RecordingName = RecordingName,
                CreatedAt = now.AddHours(-1),
            };
        }
    }
}
