using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Verbara.Platform.Api.Health;
using Verbara.Platform.Api.Services;
using Verbara.Sdk.Enums;

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
    /// <paramref name="amiHostname"/> is <c>Asterisk:Ami:Hostname</c>; an empty value is a digital-only pod.
    /// </summary>
    internal sealed class ReadinessFactory(
        Action<IServiceCollection>? extraServices = null, string amiHostname = AmiHostname)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Asterisk:Ami:Hostname", amiHostname);
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

    /// <summary>Replaces the voice connection seam with a fixed reading (the Testing host holds no primary server).</summary>
    private static Action<IServiceCollection> VoiceConnection(FixedVoiceConnectionStatus connection) =>
        services =>
        {
            foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IVoiceConnectionStatus)).ToList())
                services.Remove(descriptor);
            services.AddSingleton<IVoiceConnectionStatus>(connection);
        };

    private static Task<(HttpStatusCode Status, string Body)> GetReadyAsync(ReadinessFactory factory) =>
        GetAsync(factory, "/health/ready");

    private static async Task<(HttpStatusCode Status, string Body)> GetAsync(ReadinessFactory factory, string path)
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(path);
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

    // ─── /health/voice (new capability, task 2.1): the voice plane, reported where no probe reads it ──────────

    [Fact]
    public async Task HealthVoice_ShouldReturn503WithOnlyTheVoiceEntries_WhenSessionFedEngineCheckUnhealthy()
    {
        // Spec requirement 1, the session-fed scenario's last line: the entry readiness no longer shows is
        // reported on /health/voice, which answers 503. The readiness entries are not part of that body. AMI reads
        // Connected, so `asterisk` is Healthy and the 503 can only come from the stub (design D8).
        await using var factory = new ReadinessFactory(services =>
        {
            VoiceConnection(FixedVoiceConnectionStatus.Configured(AmiConnectionState.Connected))(services);
            ReadyStub(
                "eventstore-subscriber",
                () => HealthCheckResult.Unhealthy("eventstore-subscriber: no session event for 31s"))(services);
        });

        var (status, body) = await GetAsync(factory, "/health/voice");

        status.Should().Be(HttpStatusCode.ServiceUnavailable,
            "an Unhealthy voice-plane check makes /health/voice answer 503; body: {0}", body);
        using var doc = JsonDocument.Parse(body);
        Checks(doc).GetProperty("eventstore-subscriber").GetProperty("status").GetString().Should().Be("Unhealthy",
            "the session-fed engine is reported on /health/voice; body: {0}", body);
        Checks(doc).GetProperty("asterisk").GetProperty("status").GetString().Should().Be("Healthy",
            "the 503 is the session-fed engine's, not the asterisk entry's; body: {0}", body);
        foreach (var readinessEntry in new[] { "services", "retention", "csatrunner-orchestrator" })
        {
            Checks(doc).TryGetProperty(readinessEntry, out _).Should().BeFalse(
                "{0} is a readiness check and is not reported on /health/voice; body: {1}", readinessEntry, body);
        }
    }

    [Fact]
    public async Task HealthVoice_ShouldReturn200_WhenSessionFedEngineCheckDegraded()
    {
        // The default status mapping, as on /health/ready: Degraded answers 200, only Unhealthy answers 503. AMI reads
        // Connected, so the stub is the only voice-plane entry that is not Healthy.
        await using var factory = new ReadinessFactory(services =>
        {
            VoiceConnection(FixedVoiceConnectionStatus.Configured(AmiConnectionState.Connected))(services);
            ReadyStub(
                "analytics-aggregator",
                () => HealthCheckResult.Degraded("analytics-aggregator: no session event yet"))(services);
        });

        var (status, body) = await GetAsync(factory, "/health/voice");

        status.Should().Be(HttpStatusCode.OK, "Degraded maps to 200 on /health/voice; body: {0}", body);
        using var doc = JsonDocument.Parse(body);
        Checks(doc).GetProperty("analytics-aggregator").GetProperty("status").GetString().Should().Be("Degraded",
            "body: {0}", body);
    }

    // ─── /health/voice, the `asterisk` entry (task 2.2, design D2; spec requirement 4) ───────────────────────

    [Fact]
    public async Task HealthVoice_ShouldReturn200WithAsteriskHealthy_WhenAmiNotConfigured()
    {
        // A digital-only pod: `Asterisk:Ami:Hostname` empty, read through the real seam.
        await using var factory = new ReadinessFactory(amiHostname: string.Empty);

        var (status, body) = await GetAsync(factory, "/health/voice");

        status.Should().Be(HttpStatusCode.OK, "a digital-only pod has a healthy voice plane; body: {0}", body);
        using var doc = JsonDocument.Parse(body);
        var asterisk = Checks(doc).GetProperty("asterisk");
        asterisk.GetProperty("status").GetString().Should().Be("Healthy", "body: {0}", body);
        asterisk.GetProperty("description").GetString().Should().Contain("not configured", "body: {0}", body);
    }

    [Fact]
    public async Task HealthVoice_ShouldReturn503WithTheConnectionStateAndNoHostOrPort_WhenPrimaryConnectionReconnecting()
    {
        await using var factory = new ReadinessFactory(
            VoiceConnection(FixedVoiceConnectionStatus.Configured(AmiConnectionState.Reconnecting)));

        var (status, body) = await GetAsync(factory, "/health/voice");

        status.Should().Be(HttpStatusCode.ServiceUnavailable,
            "a down AMI connection makes /health/voice answer 503; body: {0}", body);
        using var doc = JsonDocument.Parse(body);
        var asterisk = Checks(doc).GetProperty("asterisk");
        asterisk.GetProperty("status").GetString().Should().Be("Unhealthy", "body: {0}", body);
        asterisk.GetProperty("data").GetProperty("connectionState").GetString().Should().Be("Reconnecting",
            "the entry's data carries the connection state; body: {0}", body);
        AssertNoAmiEndpoint(doc, body);
    }

    [Fact]
    public async Task HealthVoice_ShouldReturn503WithNoPrimaryServerAndNoHostOrPort_WhenAmiConfiguredAndPoolHoldsNoPrimary()
    {
        // The real seam over the real configuration: the Testing host sets `Asterisk:Ami:Hostname` but its stubbed
        // hosted services never seed the pool, so it holds no primary server. This is the path that can read the
        // configured endpoint, so it is the one that must not print it.
        await using var factory = new ReadinessFactory();

        var (status, body) = await GetAsync(factory, "/health/voice");

        status.Should().Be(HttpStatusCode.ServiceUnavailable,
            "AMI configured with no primary server is a down voice plane; body: {0}", body);
        using var doc = JsonDocument.Parse(body);
        var asterisk = Checks(doc).GetProperty("asterisk");
        asterisk.GetProperty("status").GetString().Should().Be("Unhealthy", "body: {0}", body);
        asterisk.GetProperty("data").GetProperty("connectionState").GetString()
            .Should().Be(VoiceConnectionSnapshot.NoPrimaryServer, "body: {0}", body);
        AssertNoAmiEndpoint(doc, body);
    }

    [Fact]
    public async Task HealthVoice_ShouldReturn200_WhenPrimaryConnectionConnectedAndOtherVoiceChecksDegraded()
    {
        await using var factory = new ReadinessFactory(services =>
        {
            VoiceConnection(FixedVoiceConnectionStatus.Configured(AmiConnectionState.Connected))(services);
            ReadyStub("analytics-aggregator", () => HealthCheckResult.Degraded("analytics-aggregator: no session event yet"))(services);
        });

        var (status, body) = await GetAsync(factory, "/health/voice");

        status.Should().Be(HttpStatusCode.OK, "a connected AMI is a healthy voice plane; body: {0}", body);
        using var doc = JsonDocument.Parse(body);
        Checks(doc).GetProperty("asterisk").GetProperty("status").GetString().Should().Be("Healthy", "body: {0}", body);
    }

    [Fact]
    public async Task HealthVoice_ShouldReturn503NamingTheWorker_WhenConnectedButVoiceInboundHeartbeatStale()
    {
        await using var factory = new ReadinessFactory(
            VoiceConnection(FixedVoiceConnectionStatus.Configured(AmiConnectionState.Connected)));
        factory.Heartbeat.RecordTickAt(
            nameof(StasisInboundConsumer), TimeSpan.FromSeconds(1), DateTimeOffset.UtcNow - TimeSpan.FromHours(2));

        var (status, body) = await GetAsync(factory, "/health/voice");

        status.Should().Be(HttpStatusCode.ServiceUnavailable,
            "a stalled voice inbound worker is a down voice plane; body: {0}", body);
        using var doc = JsonDocument.Parse(body);
        var asterisk = Checks(doc).GetProperty("asterisk");
        asterisk.GetProperty("status").GetString().Should().Be("Unhealthy", "body: {0}", body);
        asterisk.GetProperty("description").GetString().Should().Contain(nameof(StasisInboundConsumer),
            "the entry names the voice inbound worker; body: {0}", body);
    }

    [Fact]
    public async Task HealthVoice_ShouldReturn200WithNoCircuitData_WhenConnectedAndAmiCircuitOpenPastThreshold()
    {
        // The dead circuit overlay is gone (design D2): a seeded `asterisk.ami` key no longer reaches the check.
        await using var factory = new ReadinessFactory(
            VoiceConnection(FixedVoiceConnectionStatus.Configured(AmiConnectionState.Connected)));
        factory.Observer.SetForTest(
            "asterisk.ami", ResilienceCircuitState.Open, DateTimeOffset.UtcNow - PastEscalationThreshold);

        var (status, body) = await GetAsync(factory, "/health/voice");

        status.Should().Be(HttpStatusCode.OK, "no circuit feeds the asterisk check; body: {0}", body);
        using var doc = JsonDocument.Parse(body);
        var asterisk = Checks(doc).GetProperty("asterisk");
        asterisk.GetProperty("status").GetString().Should().Be("Healthy", "body: {0}", body);
        asterisk.GetProperty("data").TryGetProperty("asterisk.ami", out _).Should().BeFalse(
            "the circuit keys are no longer part of the entry; body: {0}", body);
    }

    /// <summary>
    /// The endpoint is anonymous, so no string in the body may carry the AMI endpoint: neither the hostname the host
    /// configures nor the defaults the old check printed (<c>localhost:5038</c>, see tasks.md, Notes for Phase B).
    /// Numbers are left out of the port check: a duration such as <c>0.15038</c> is not a port.
    /// </summary>
    private static void AssertNoAmiEndpoint(JsonDocument doc, string body)
    {
        body.Should().NotContain(AmiHostname, "the configured AMI hostname must not reach the body");
        body.Should().NotContain("localhost", "the default AMI hostname must not reach the body");
        foreach (var text in Strings(doc.RootElement))
            text.Should().NotContain("5038", "no string in the body carries the AMI port; body: {0}", body);
    }

    /// <summary>Every property name and string value in <paramref name="element"/>, depth first.</summary>
    private static IEnumerable<string> Strings(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                yield return element.GetString()!;
                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    yield return property.Name;
                    foreach (var text in Strings(property.Value))
                        yield return text;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var text in Strings(item))
                        yield return text;
                }
                break;
        }
    }
}
