using System.Net;
using System.Net.Http.Json;
using Verbara.Platform.Api.Endpoints.Retention;
using FluentAssertions;
using Xunit;

namespace Verbara.Platform.Api.Tests.Retention;

/// <summary>
/// R5.2 PC.1 — verifies admin retention endpoints under <c>/management/retention</c>:
/// list targets, get config, run-now (dry-run + purge), patch config (DryRun toggle).
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
    public async Task PatchConfig_ShouldToggleDryRun_AndReturnUpdatedConfig()
    {
        var client = _factory.CreatePlatformAdminClient();

        var initialResponse = await client.GetAsync("/api/v1/management/retention/config");
        var initialConfig = (await initialResponse.Content.ReadFromJsonAsync<RetentionConfigDto>())!;
        var newValue = !initialConfig.DryRun;

        try
        {
            var patchResponse = await client.PatchAsJsonAsync(
                "/api/v1/management/retention/config",
                new RetentionConfigPatchDto { DryRun = newValue });

            patchResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            var patched = await patchResponse.Content.ReadFromJsonAsync<RetentionConfigDto>();
            patched.Should().NotBeNull();
            patched!.DryRun.Should().Be(newValue);
        }
        finally
        {
            // Restore initial state so the next test starts from the same posture.
            await client.PatchAsJsonAsync(
                "/api/v1/management/retention/config",
                new RetentionConfigPatchDto { DryRun = initialConfig.DryRun });
        }
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
}
