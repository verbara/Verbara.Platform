using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Platform.Surveys;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// csat-runner Phase B — CSAT capture + analytics endpoint contracts. Covers the
/// 4 endpoints (webchat / email / sms capture + queue analytics read), the
/// <see cref="LicenseFeature.CsatRunner"/> 402 gate, the <c>csat</c>-category audit
/// row, and <c>responseToken</c> validation (missing / malformed / expired /
/// tenant-queue mismatch / rating out of range).
/// </summary>
public sealed class CsatResponseEndpointsTests : IDisposable
{
    private const string TestTokenSecret = "csat-webchat-test-secret-000000000000";
    private const string TestQueue = "support-tier1";
    private const string TestSurveyId = "srv-csat-v1";

    private readonly AuthenticatedPlatformApiFactory _factory;

    public CsatResponseEndpointsTests()
    {
        _factory = new AuthenticatedPlatformApiFactory();
    }

    public void Dispose() => _factory.Dispose();

    // ── Factory with a known token secret + captured audit ─────────────────────
    // WithWebHostBuilder returns a DelegatedWebApplicationFactory<Program> (NOT the
    // derived type), so callers use the base WebApplicationFactory surface + the
    // header helpers below rather than CreateAuthenticatedClient().

    private (WebApplicationFactory<Program> factory, IAuditService audit) BuildLicensedFactory()
    {
        var audit = Substitute.For<IAuditService>();
        var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                RemoveAll<ICsatWebChatTokenVerifier>(services);
                services.AddSingleton<ICsatWebChatTokenVerifier>(
                    new HmacCsatWebChatTokenVerifier(Encoding.UTF8.GetBytes(TestTokenSecret)));

                RemoveAll<IAuditService>(services);
                services.AddSingleton(audit);
            }));
        return (factory, audit);
    }

    private static NoCsatLicenseFactory BuildUnlicensedFactory() => new();

    private static void RemoveAll<T>(IServiceCollection services)
    {
        foreach (var d in services.Where(d => d.ServiceType == typeof(T)).ToList())
            services.Remove(d);
    }

    private static HttpClient AnonymousClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", AuthenticatedPlatformApiFactory.TestTenantId);
        return client;
    }

    private static HttpClient AuthenticatedClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {AuthenticatedPlatformApiFactory.TestApiKey}");
        client.DefaultRequestHeaders.Add("X-Tenant-Id", AuthenticatedPlatformApiFactory.TestTenantId);
        return client;
    }

    // ── Token minting (mirrors HmacCsatWebChatTokenVerifier's v1.{payload}.{sig}) ──

    private static string MintToken(
        string tenantId,
        string queueName,
        string channel,
        DateTimeOffset issuedAt,
        string secret = TestTokenSecret,
        string surveyId = TestSurveyId)
    {
        var payloadJson =
            $"{{\"tid\":\"{tenantId}\",\"svi\":\"{surveyId}\",\"q\":\"{queueName}\",\"ch\":\"{channel}\",\"iat\":{issuedAt.ToUnixTimeSeconds()}}}";
        var payloadB64 = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payloadJson));
        var signedPrefix = $"v1.{payloadB64}";
        var sig = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(signedPrefix));
        var sigB64 = Base64Url.EncodeToString(sig);
        return $"{signedPrefix}.{sigB64}";
    }

    private static object WebChatBody(string token, int rating = 5, string queue = TestQueue, string channel = "webchat") => new
    {
        responseToken = token,
        surveyId = TestSurveyId,
        questionId = SurveyQuestionIds.CsatRating,
        channel,
        queueName = queue,
        rating,
        comment = "Fast and friendly, thanks!",
        capturedAt = "2026-07-07T09:15:00Z",
        conversationId = "conv-8f2a1c4e",
    };

    // ── Endpoint contract: webchat capture happy path ──────────────────────────

    [Fact]
    public async Task PostWebChat_ShouldPersistSurveyResponse_WhenTokenValid()
    {
        var (factory, _) = BuildLicensedFactory();
        using var client = AnonymousClient(factory);
        var token = MintToken(AuthenticatedPlatformApiFactory.TestTenantId, TestQueue, "webchat", DateTimeOffset.UtcNow);

        var resp = await client.PostAsJsonAsync("/api/v1/csat/responses/webchat", WebChatBody(token));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ISurveyResponseStore>();
        var rows = await store.GetByConversationAsync(
            new TenantId(AuthenticatedPlatformApiFactory.TestTenantId),
            EntityId.From("conv-8f2a1c4e"),
            CancellationToken.None);

        rows.Should().ContainSingle();
        var row = rows[0];
        row.Rating.Should().Be(5);
        row.Channel.Should().Be("webchat");
        row.QueueName.Should().Be(TestQueue);
        row.Comment.Should().Be("Fast and friendly, thanks!");
    }

    [Fact]
    public async Task PostWebChat_ShouldWriteCsatAuditRow_WhenTokenValid()
    {
        var (factory, audit) = BuildLicensedFactory();
        using var client = AnonymousClient(factory);
        var token = MintToken(AuthenticatedPlatformApiFactory.TestTenantId, TestQueue, "webchat", DateTimeOffset.UtcNow);

        var resp = await client.PostAsJsonAsync("/api/v1/csat/responses/webchat", WebChatBody(token));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        await audit.Received(1).RecordAsync(
            Arg.Is<TenantId>(t => t.Value == AuthenticatedPlatformApiFactory.TestTenantId),
            category: "csat",
            action: Arg.Any<string>(),
            severity: Arg.Any<string>(),
            actorId: Arg.Any<string>(),
            actorType: Arg.Any<string>(),
            targetId: Arg.Any<string?>(),
            targetType: Arg.Any<string?>(),
            correlationId: Arg.Any<Guid?>(),
            changes: Arg.Any<AuditChanges?>(),
            metadata: Arg.Any<IReadOnlyDictionary<string, string>?>(),
            retainUntil: Arg.Any<DateTimeOffset?>(),
            ct: Arg.Any<CancellationToken>());
    }

    // ── License gate (402 + RFC 9457 ProblemDetails) ───────────────────────────

    [Fact]
    public async Task PostWebChat_ShouldReturn402ProblemDetails_WhenCsatRunnerUnlicensed()
    {
        using var factory = BuildUnlicensedFactory();
        using var client = AnonymousClient(factory);
        var token = MintToken(AuthenticatedPlatformApiFactory.TestTenantId, TestQueue, "webchat", DateTimeOffset.UtcNow);

        var resp = await client.PostAsJsonAsync("/api/v1/csat/responses/webchat", WebChatBody(token));

        resp.StatusCode.Should().Be(HttpStatusCode.PaymentRequired);
        resp.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("status").GetInt32().Should().Be(402);

        // Gate short-circuits before the handler — nothing is persisted.
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ISurveyResponseStore>();
        var rows = await store.GetByConversationAsync(
            new TenantId(AuthenticatedPlatformApiFactory.TestTenantId),
            EntityId.From("conv-8f2a1c4e"),
            CancellationToken.None);
        rows.Should().BeEmpty();
    }

    // ── responseToken validation ───────────────────────────────────────────────

    [Fact]
    public async Task PostWebChat_ShouldReturn401_WhenTokenMissing()
    {
        var (factory, _) = BuildLicensedFactory();
        using var client = AnonymousClient(factory);

        var resp = await client.PostAsJsonAsync("/api/v1/csat/responses/webchat", WebChatBody(token: ""));

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PostWebChat_ShouldReturn401_WhenTokenMalformed()
    {
        var (factory, _) = BuildLicensedFactory();
        using var client = AnonymousClient(factory);

        var resp = await client.PostAsJsonAsync(
            "/api/v1/csat/responses/webchat", WebChatBody(token: "not-a-valid-token"));

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PostWebChat_ShouldReturn401_WhenTokenExpired()
    {
        var (factory, _) = BuildLicensedFactory();
        using var client = AnonymousClient(factory);
        // Issued 8 days ago — beyond the 7-day TTL.
        var expired = MintToken(
            AuthenticatedPlatformApiFactory.TestTenantId, TestQueue, "webchat",
            DateTimeOffset.UtcNow - TimeSpan.FromDays(8));

        var resp = await client.PostAsJsonAsync("/api/v1/csat/responses/webchat", WebChatBody(expired));

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PostWebChat_ShouldReturn401_WhenSignatureTampered()
    {
        var (factory, _) = BuildLicensedFactory();
        using var client = AnonymousClient(factory);
        // Minted under a DIFFERENT secret — the signature will not verify.
        var forged = MintToken(
            AuthenticatedPlatformApiFactory.TestTenantId, TestQueue, "webchat",
            DateTimeOffset.UtcNow, secret: "a-different-secret-that-will-not-verify");

        var resp = await client.PostAsJsonAsync("/api/v1/csat/responses/webchat", WebChatBody(forged));

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PostWebChat_ShouldReturn401_WhenSignedQueueMismatchesBody()
    {
        var (factory, _) = BuildLicensedFactory();
        using var client = AnonymousClient(factory);
        // Token signs queue "other-queue" but the body submits TestQueue.
        var token = MintToken(AuthenticatedPlatformApiFactory.TestTenantId, "other-queue", "webchat", DateTimeOffset.UtcNow);

        var resp = await client.PostAsJsonAsync(
            "/api/v1/csat/responses/webchat", WebChatBody(token, queue: TestQueue));

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public async Task PostWebChat_ShouldReturn400_WhenRatingOutOfRange(int rating)
    {
        var (factory, _) = BuildLicensedFactory();
        using var client = AnonymousClient(factory);
        var token = MintToken(AuthenticatedPlatformApiFactory.TestTenantId, TestQueue, "webchat", DateTimeOffset.UtcNow);

        var resp = await client.PostAsJsonAsync(
            "/api/v1/csat/responses/webchat", WebChatBody(token, rating: rating));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── Internal email / sms capture (service-key gated) ───────────────────────

    [Fact]
    public async Task PostEmail_ShouldReturn401_WhenServiceKeyMissing()
    {
        var (factory, _) = BuildLicensedFactory();
        using var client = AnonymousClient(factory);
        var token = MintToken(AuthenticatedPlatformApiFactory.TestTenantId, TestQueue, "email", DateTimeOffset.UtcNow);

        var resp = await client.PostAsJsonAsync(
            "/api/v1/csat/responses/email", WebChatBody(token, channel: "email"));

        // The X-Service-Key filter runs after the license gate; without the key it is 401
        // (or 503 when no service key is configured for the test host).
        resp.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.ServiceUnavailable);
    }

    // ── Analytics read (SupervisorPlus) ────────────────────────────────────────

    [Fact]
    public async Task GetQueueCsatSummary_ShouldReturnSummary_WhenSupervisorAuthorized()
    {
        var (factory, _) = BuildLicensedFactory();
        using var client = AuthenticatedClient(factory);

        var resp = await client.GetAsync($"/api/v1/analytics/csat/queues/{TestQueue}?range=24h&channel=webchat");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("queueName").GetString().Should().Be(TestQueue);
        doc.RootElement.GetProperty("channel").GetString().Should().Be("webchat");
    }

    [Fact]
    public async Task GetQueueCsatSummary_ShouldReturn402_WhenCsatRunnerUnlicensed()
    {
        using var factory = BuildUnlicensedFactory();
        using var client = AuthenticatedClient(factory);

        var resp = await client.GetAsync($"/api/v1/analytics/csat/queues/{TestQueue}?range=24h");

        resp.StatusCode.Should().Be(HttpStatusCode.PaymentRequired);
    }

    // ── Scope-wide aggregate read (csat-completion) ────────────────────────────

    private static async Task SeedResponseAsync(
        WebApplicationFactory<Program> factory, string queue, string channel, int rating, string conversationId)
    {
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ISurveyResponseStore>();
        var now = DateTimeOffset.UtcNow;
        await store.SaveAsync(new SurveyResponse
        {
            ResponseId = EntityId.New(),
            SurveyId = EntityId.From(TestSurveyId),
            TenantId = new TenantId(AuthenticatedPlatformApiFactory.TestTenantId),
            ConversationId = EntityId.From(conversationId),
            ContactId = EntityId.From(conversationId),
            Answers = [new SurveyAnswer(EntityId.From(SurveyQuestionIds.CsatRating), rating.ToString(System.Globalization.CultureInfo.InvariantCulture))],
            SubmittedAt = now,
            Channel = channel,
            QueueName = queue,
            Rating = rating,
            CapturedAt = now,
        }, CancellationToken.None);
    }

    [Fact]
    public async Task GetScopeCsatAggregate_ShouldRollUpAcrossQueues_WhenSupervisorAuthorized()
    {
        var (factory, _) = BuildLicensedFactory();
        using var client = AuthenticatedClient(factory);

        await SeedResponseAsync(factory, "support-tier1", "webchat", 5, "conv-agg-1");
        await SeedResponseAsync(factory, "support-tier1", "webchat", 4, "conv-agg-2");
        await SeedResponseAsync(factory, "billing", "webchat", 3, "conv-agg-3");

        var resp = await client.GetAsync("/api/v1/analytics/csat?range=24h");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        // Envelope sums the scope: 3 responses; the queues[] carry the CsatResponseDto shape with channel 'all'.
        root.GetProperty("totalResponses").GetInt32().Should().Be(3);
        var queues = root.GetProperty("queues");
        queues.GetArrayLength().Should().Be(2);
        foreach (var q in queues.EnumerateArray())
        {
            q.GetProperty("channel").GetString().Should().Be("all");
            q.GetProperty("queueName").GetString().Should().NotBeNullOrEmpty();
            q.TryGetProperty("totalResponses", out _).Should().BeTrue();
            q.TryGetProperty("averageRating", out _).Should().BeTrue();
            q.TryGetProperty("rangeStart", out _).Should().BeTrue();
            q.TryGetProperty("rangeEnd", out _).Should().BeTrue();
        }
    }

    [Fact]
    public async Task GetScopeCsatAggregate_ShouldEchoChannelFilter_WhenChannelQueried()
    {
        var (factory, _) = BuildLicensedFactory();
        using var client = AuthenticatedClient(factory);

        await SeedResponseAsync(factory, "support-tier1", "voice", 4, "conv-vagg-1");
        await SeedResponseAsync(factory, "support-tier1", "webchat", 2, "conv-vagg-2");

        var resp = await client.GetAsync("/api/v1/analytics/csat?channel=voice");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        // Only the voice response counts, and every queues[] row echoes 'voice'.
        root.GetProperty("totalResponses").GetInt32().Should().Be(1);
        foreach (var q in root.GetProperty("queues").EnumerateArray())
            q.GetProperty("channel").GetString().Should().Be("voice");
    }

    [Fact]
    public async Task GetScopeCsatAggregate_ShouldReturn402_WhenCsatRunnerUnlicensed()
    {
        using var factory = BuildUnlicensedFactory();
        using var client = AuthenticatedClient(factory);

        var resp = await client.GetAsync("/api/v1/analytics/csat?range=24h");

        resp.StatusCode.Should().Be(HttpStatusCode.PaymentRequired);
    }
}

/// <summary>
/// Variant of <see cref="AuthenticatedPlatformApiFactory"/> that keeps the operational
/// Customer tenant + admin auth wiring but reports NO licensed Pro features
/// (<c>AddNoProFeaturesLicensed()</c>), driving the <c>LicenseFeature.CsatRunner</c> 402
/// path for the CSAT endpoints. Also installs a known-secret webchat token verifier so a
/// well-formed token reaches the gate. Mirrors <c>NoTypificationLicenseFactory</c> — the
/// reliable HTTP-level 402 pattern is a dedicated factory subclass, not WithWebHostBuilder.
/// </summary>
internal sealed class NoCsatLicenseFactory : WebApplicationFactory<Program>
{
    private const string TestTokenSecret = "csat-webchat-test-secret-000000000000";
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

            services.AddNoProFeaturesLicensed();
            if (!services.Any(d => d.ServiceType == typeof(byte[])))
                services.AddSingleton<byte[]>([]);

            foreach (var d in services.Where(d => d.ServiceType == typeof(ICsatWebChatTokenVerifier)).ToList())
                services.Remove(d);
            services.AddSingleton<ICsatWebChatTokenVerifier>(
                new HmacCsatWebChatTokenVerifier(System.Text.Encoding.UTF8.GetBytes(TestTokenSecret)));

            AuthenticatedPlatformApiFactory.RegisterInMemoryStores(services);
        });

        var host = base.CreateHost(builder);

        AuthenticatedPlatformApiFactory.SeedEnterpriseFeatureGate(host.Services, AuthenticatedPlatformApiFactory.TestTenantId);
        AuthenticatedPlatformApiFactory.SeedTestCustomerTenant(host.Services, AuthenticatedPlatformApiFactory.TestTenantId);

        return host;
    }
}
