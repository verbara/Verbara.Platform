using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Verbara.Platform.Api.Health;
using Verbara.Platform.Api.Services;

namespace Verbara.Platform.Api.Tests.Health;

/// <summary>
/// readiness-without-asterisk (spec <c>api-readiness-scope</c>, Platform/ADR-0025 addendum 2026-10-13): what
/// <c>GET /health/ready</c> may depend on. The readiness probe answers "can this pod serve the HTTP plane";
/// the voice plane (AMI, the call-session stream, the Stasis heartbeat) and dependency circuits never gate it,
/// while the database and the digital worker heartbeats keep gating it.
///
/// <para>Every test drives the real host through <see cref="WebApplicationFactory{TEntryPoint}"/>, in the
/// <see cref="CommunityBootReadinessTests"/> shape, with <c>Asterisk:Ami:Hostname</c> set (design D8). Circuit
/// state is seeded with <see cref="ResilienceStateObserver.SetForTest"/>: the observer's hosted registration is
/// removed by <see cref="AuthenticatedPlatformApiFactory.StubVerbaraHostedServices"/>, so its timer never starts
/// and nothing overwrites the seeded snapshot. Each assertion carries the response body, so a failure names the
/// entry that produced the status.</para>
/// </summary>
public sealed class ReadinessScopeTests
{
    private const string AmiHostname = "ami.readiness-scope.invalid";

    private static readonly TimeSpan PastEscalationThreshold = TimeSpan.FromMinutes(6);

    /// <summary>
    /// The readiness host: AMI configured, Asterisk hosted services stubbed, in-memory stores, unlicensed (the
    /// community boot). <paramref name="extraServices"/> registers the stub checks a test needs.
    /// </summary>
    private sealed class ReadinessFactory(Action<IServiceCollection>? extraServices = null)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Asterisk:Ami:Hostname", AmiHostname);
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.UseEnvironment("Testing");

            builder.ConfigureServices(services =>
            {
                AuthenticatedPlatformApiFactory.StubVerbaraHostedServices(services);
                AuthenticatedPlatformApiFactory.RegisterInMemoryStores(services);

                if (!services.Any(d => d.ServiceType == typeof(byte[])))
                    services.AddSingleton<byte[]>([]);

                extraServices?.Invoke(services);
            });

            return base.CreateHost(builder);
        }

        internal ResilienceStateObserver Observer => Services.GetRequiredService<ResilienceStateObserver>();

        internal ServiceHeartbeat Heartbeat => (ServiceHeartbeat)Services.GetRequiredService<IServiceHeartbeat>();
    }

    /// <summary>Minimal AOT-safe <see cref="IHealthCheck"/> returning a fixed result.</summary>
    private sealed class DelegateHealthCheck(Func<HealthCheckResult> result) : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(result());
    }

    private static Action<IServiceCollection> ReadyStub(string name, Func<HealthCheckResult> result) =>
        services => services.AddHealthChecks().AddCheck(name, new DelegateHealthCheck(result), tags: ["ready"]);

    private static async Task<(HttpStatusCode Status, string Body)> GetReadyAsync(ReadinessFactory factory)
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health/ready");
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static JsonElement Checks(JsonDocument doc) => doc.RootElement.GetProperty("checks");

    // ─── Red against the unfixed code: each asserts the FIXED behaviour ─────────────────────────────

    [Fact]
    public async Task HealthReady_ShouldReturn200_WhenAmiCircuitOpenPastThreshold()
    {
        // T1 — the plan row's red test. It pins the contract, not a reachable state: nothing executes a policy
        // under `asterisk.ami` today (design, Context).
        await using var factory = new ReadinessFactory();
        factory.Observer.SetForTest(
            "asterisk.ami", ResilienceCircuitState.Open, DateTimeOffset.UtcNow - PastEscalationThreshold);

        var (status, body) = await GetReadyAsync(factory);

        status.Should().Be(HttpStatusCode.OK,
            "a voice circuit open past five minutes must not drain the pod; body: {0}", body);
        using var doc = JsonDocument.Parse(body);
        Checks(doc).TryGetProperty("asterisk", out _).Should().BeFalse(
            "the asterisk check is voice-scoped and reported on /health/voice; body: {0}", body);
    }

    [Fact]
    public async Task HealthReady_ShouldReturn200_WhenSessionFedEngineCheckUnhealthy()
    {
        // T2 — the live path. The stub stands in for Pro's producer (it registers the real name with the tag
        // `ready`); the classification under test is Platform's.
        await using var factory = new ReadinessFactory(ReadyStub(
            "eventstore-subscriber",
            () => HealthCheckResult.Unhealthy("eventstore-subscriber: no session event for 31s")));

        var (status, body) = await GetReadyAsync(factory);

        status.Should().Be(HttpStatusCode.OK,
            "a session-fed engine check is voice-plane and must not drain the pod; body: {0}", body);
        using var doc = JsonDocument.Parse(body);
        Checks(doc).TryGetProperty("eventstore-subscriber", out _).Should().BeFalse(
            "the session-fed engine checks are reported on /health/voice; body: {0}", body);
    }

    [Fact]
    public async Task HealthReady_ShouldReturn200_WhenWebhookDeliveryCircuitOpenPastThreshold()
    {
        // T3 — one tenant's unreachable receiver opens a global key; draining the pod heals nothing.
        await using var factory = new ReadinessFactory();
        factory.Observer.SetForTest(
            "webhook.delivery", ResilienceCircuitState.Open, DateTimeOffset.UtcNow - PastEscalationThreshold);

        var (status, body) = await GetReadyAsync(factory);

        status.Should().Be(HttpStatusCode.OK,
            "a dependency circuit makes services Degraded at most; body: {0}", body);
        using var doc = JsonDocument.Parse(body);
        Checks(doc).GetProperty("services").GetProperty("status").GetString().Should().Be("Degraded",
            "the open circuit stays visible as Degraded; body: {0}", body);
    }

    [Fact]
    public async Task HealthReady_ShouldReturn200_WhenOidcCircuitOpenPastThreshold()
    {
        // T4 — an identity-provider outage must fail SSO logins, not every tenant's password login.
        await using var factory = new ReadinessFactory();
        factory.Observer.SetForTest(
            "oidc.token-exchange", ResilienceCircuitState.Open, DateTimeOffset.UtcNow - PastEscalationThreshold);

        var (status, body) = await GetReadyAsync(factory);

        status.Should().Be(HttpStatusCode.OK,
            "a dependency circuit makes services Degraded at most; body: {0}", body);
        using var doc = JsonDocument.Parse(body);
        Checks(doc).GetProperty("services").GetProperty("status").GetString().Should().Be("Degraded",
            "the open circuit stays visible as Degraded; body: {0}", body);
    }

    [Fact]
    public async Task HealthReady_ShouldReturn200_WhenVoiceInboundHeartbeatStale()
    {
        // T5 — the Stasis worker's heartbeat is voice-plane. A digital heartbeat is recorded fresh (a one-hour
        // interval, so a slow runner cannot make it stale) to prove the 503 is not a digital worker's.
        await using var factory = new ReadinessFactory();
        var now = DateTimeOffset.UtcNow;
        factory.Heartbeat.RecordTickAt(nameof(StasisInboundConsumer), TimeSpan.FromSeconds(1), now - TimeSpan.FromHours(2));
        factory.Heartbeat.RecordTickAt(nameof(QueueDistributionWorker), TimeSpan.FromHours(1), now);

        var (status, body) = await GetReadyAsync(factory);

        status.Should().Be(HttpStatusCode.OK,
            "the voice inbound worker's heartbeat must not drain the pod; body: {0}", body);
        using var doc = JsonDocument.Parse(body);
        var heartbeats = Checks(doc).GetProperty("services").GetProperty("data").GetProperty("heartbeats");
        heartbeats.TryGetProperty(nameof(StasisInboundConsumer), out _).Should().BeFalse(
            "the services heartbeat data does not list the voice inbound worker; body: {0}", body);
    }

    // ─── Poles that are green before and after the fix ───────────────────────────────────────────────

    [Fact]
    public async Task HealthReady_ShouldReturn503_WhenPostgresUnhealthy()
    {
        // N1 — the Testing host has no connection string, so Program.cs registers no `postgres` check; the stub
        // stands in for it under the real name and tag.
        await using var factory = new ReadinessFactory(ReadyStub(
            "postgres", () => HealthCheckResult.Unhealthy("Postgres unreachable")));

        var (status, body) = await GetReadyAsync(factory);

        status.Should().Be(HttpStatusCode.ServiceUnavailable,
            "a database outage still drains the pod; body: {0}", body);
        using var doc = JsonDocument.Parse(body);
        Checks(doc).GetProperty("postgres").GetProperty("status").GetString().Should().Be("Unhealthy",
            "the 503 must come from the postgres entry; body: {0}", body);
    }

    [Fact]
    public async Task HealthReady_ShouldReturn503_WhenDigitalWorkerHeartbeatStale()
    {
        // N2 — a stalled digital worker still drains the pod, and `services` names it.
        await using var factory = new ReadinessFactory();
        factory.Heartbeat.RecordTickAt(
            nameof(QueueDistributionWorker), TimeSpan.FromSeconds(1), DateTimeOffset.UtcNow - TimeSpan.FromHours(2));

        var (status, body) = await GetReadyAsync(factory);

        status.Should().Be(HttpStatusCode.ServiceUnavailable,
            "a stalled digital worker still drains the pod; body: {0}", body);
        using var doc = JsonDocument.Parse(body);
        var services = Checks(doc).GetProperty("services");
        services.GetProperty("status").GetString().Should().Be("Unhealthy", "body: {0}", body);
        services.GetProperty("description").GetString().Should().Contain(nameof(QueueDistributionWorker),
            "the services entry names the stalled worker; body: {0}", body);
    }
}
