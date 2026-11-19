using System.Net;
using System.Net.Http.Json;
using Verbara.Platform.Api.Endpoints.Retention;
using Verbara.Platform.Audit;
using Verbara.Sdk.Pro.Storage.Common.Retention;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Verbara.Platform.Api.Tests.Retention;

/// <summary>
/// R5.2 PC.1 — verifies admin retention endpoints under <c>/management/retention</c>:
/// list targets, get config (the configured mode, read-only), run-now (dry-run + purge); the retired
/// PATCH toggle answers 405.
/// Permission gates: <c>system:retention:view</c> + <c>system:retention:manage</c> via
/// PlatformAdminRequirement (ADR-0037).
/// </summary>
public sealed class RetentionAdminEndpointsTests : IClassFixture<PlatformAdminApiFactory>
{
    private readonly PlatformAdminApiFactory _factory;

    public RetentionAdminEndpointsTests(PlatformAdminApiFactory factory) => _factory = factory;

    [Fact]
    public async Task GetTargets_ShouldReturnListOfRegisteredTargets_WhenAuthorized()
    {
        var client = _factory.CreatePlatformAdminClient();

        var response = await client.GetAsync("/api/v1/management/retention/targets");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var targets = await response.Content.ReadFromJsonAsync<RetentionTargetDto[]>();
        targets.Should().NotBeNull();
        // Test host doesn't wire Pro Postgres storage packages (no IRetentionTarget registrations);
        // empty list is the correct response. Production deploys with full Postgres wiring see
        // 5+ targets per the Pro v1.8.0-pro infrastructure (verified by smoke in PD.2).
    }

    [Fact]
    public async Task GetConfig_ShouldReturnSnapshot_WhenAuthorized()
    {
        var client = _factory.CreatePlatformAdminClient();

        var response = await client.GetAsync("/api/v1/management/retention/config");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var config = await response.Content.ReadFromJsonAsync<RetentionConfigDto>();
        config.Should().NotBeNull();
        config!.DefaultWindowDays.Should().BeGreaterThan(0);
        config.BatchSize.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task RunNow_ShouldDefaultToDryRun_AndReturnPerTargetOutcomes()
    {
        var client = _factory.CreatePlatformAdminClient();

        // No dryRun query param → defaults to true (safer posture per endpoint contract).
        var response = await client.PostAsync("/api/v1/management/retention/run-now", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<RetentionRunResultDto>();
        result.Should().NotBeNull();
        result!.DryRun.Should().BeTrue("explicit dryRun query param missing → service defaults to dry-run");
        result.Targets.Should().NotBeNull();
        result.CompletedAt.Should().BeOnOrAfter(result.StartedAt);
    }

    [Fact]
    public async Task GetConfig_ShouldReportConfiguredMode_WhenHostStartedFalse()
    {
        using var configured = _factory.WithWebHostBuilder(b => b.UseSetting(RetentionPurgeModeConfiguration.Key, "false"));
        var client = PlatformAdminApiFactory.CreatePlatformAdminClient(configured);

        var response = await client.GetAsync("/api/v1/management/retention/config");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var config = await response.Content.ReadFromJsonAsync<RetentionConfigDto>();
        config!.DryRun.Should().BeFalse("the host started with Retention:DryRun=false");
    }

    [Fact]
    public async Task GetConfig_ShouldReportDryRun_WhenKeyAbsent()
    {
        var client = _factory.CreatePlatformAdminClient();

        var config = await client.GetFromJsonAsync<RetentionConfigDto>("/api/v1/management/retention/config");

        config!.DryRun.Should().BeTrue("an absent Retention:DryRun keeps the purge dry");
    }

    [Fact]
    public async Task PatchConfig_ShouldReturn405_WhenToggleRetired()
    {
        // The admin holds system:retention:manage; the route answers 405 before any authorization runs because
        // no PATCH endpoint matches the path any more (design → Risks, measured here).
        var client = _factory.CreatePlatformAdminClient();
        using var body = new StringContent("""{"dryRun":false}""", System.Text.Encoding.UTF8, "application/json");

        var patch = await client.PatchAsync("/api/v1/management/retention/config", body);

        patch.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        var config = await client.GetFromJsonAsync<RetentionConfigDto>("/api/v1/management/retention/config");
        config!.DryRun.Should().BeTrue("the retired toggle cannot change the configured mode");
    }

    [Fact]
    public async Task RunNow_ShouldDeleteForReal_WhenDryRunFalseAndConfiguredDry()
    {
        var target = new RecordingRetentionTarget();
        var audit = new RecordingAuditService();
        using var configured = _factory.WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            services.AddSingleton<IRetentionTarget>(target);
            services.RemoveAll<IAuditService>();
            services.AddSingleton<IAuditService>(audit);
        }));
        var client = PlatformAdminApiFactory.CreatePlatformAdminClient(configured);

        var response = await client.PostAsync("/api/v1/management/retention/run-now?dryRun=false", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var result = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        result.RootElement.GetProperty("dryRun").GetBoolean().Should().BeFalse();
        var outcome = result.RootElement.GetProperty("targets").EnumerateArray()
            .Single(t => t.GetProperty("name").GetString() == "recording_target");
        outcome.GetProperty("rowsPurged").GetInt64().Should().Be(3);
        target.DryRunFlags.Should().Equal(false);
        configured.Services.GetRequiredService<IRetentionPurgeMode>().ScheduledDryRun
            .Should().BeTrue("the configured mode stays dry; the manual run states its own mode");
        audit.Entries.Should().ContainSingle(e => e.Action == "retention.manual_triggered")
            .Which.Severity.Should().Be("warning");
    }

    [Fact]
    public async Task GetTargets_ShouldReturn401_WhenUnauthenticated()
    {
        // Default unauthenticated client — no Bearer token
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/management/retention/targets");

        // 401 (no auth) — RequireAuthorization fires before the policy gate.
        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    private sealed class RecordingRetentionTarget : IRetentionTarget
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<bool> _dryRunFlags = new();

        public IReadOnlyCollection<bool> DryRunFlags => _dryRunFlags;

        public string Name => "recording_target";

        public TimeSpan? CustomWindow => TimeSpan.FromDays(1);

        public ValueTask<int> PurgeAsync(DateTimeOffset cutoff, bool dryRun, int batchSize, CancellationToken ct)
        {
            _dryRunFlags.Enqueue(dryRun);
            return ValueTask.FromResult(dryRun ? 0 : 3);
        }
    }

    private sealed class RecordingAuditService : IAuditService
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<(string Action, string Severity)> _entries = new();

        public IReadOnlyCollection<(string Action, string Severity)> Entries => _entries;

        public Task RecordAsync(
            Verbara.Platform.Core.TenantId tenantId, string category, string action, string severity, string actorId,
            string actorType, string? targetId = null, string? targetType = null, Guid? correlationId = null,
            AuditChanges? changes = null, IReadOnlyDictionary<string, string>? metadata = null,
            DateTimeOffset? retainUntil = null, CancellationToken ct = default)
        {
            _entries.Enqueue((action, severity));
            return Task.CompletedTask;
        }

        [Obsolete("Use RecordAsync for full structured audit entries.")]
        public Task LogAsync(
            Verbara.Platform.Core.TenantId tenantId, string action, string entityType, string entityId,
            string? performedBy = null, IReadOnlyDictionary<string, string>? details = null,
            CancellationToken ct = default) => Task.CompletedTask;
    }
}
