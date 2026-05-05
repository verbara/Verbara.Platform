using System.Net;
using Verbara.Platform.Audit;
using Verbara.Platform.Billing;
using Verbara.Platform.Core;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// R5.3 Phase A — Task A.7. Backend mirror of the existing
/// <c>POST /management/tenants/{id}/dunning/pause</c> endpoint.
/// Frontend S4.2 (Phase B B.2) needs a dedicated <c>resume</c> action so a
/// platform admin can clear the paused flag without relying on the legacy
/// pause-toggle semantics.
/// </summary>
public sealed class DunningResumeTests : IClassFixture<PlatformAdminApiFactory>
{
    private readonly PlatformAdminApiFactory _factory;
    private readonly HttpClient _client;

    public DunningResumeTests(PlatformAdminApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreatePlatformAdminClient();
    }

    [Fact]
    public async Task PostDunningResume_ShouldReturn200_WhenAuthorized()
    {
        const string tenantId = "dunning-resume-200";
        await SeedPausedDunningRecordAsync(tenantId);

        var response = await _client.PostAsync(
            $"/api/management/tenants/{tenantId}/dunning/resume", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(tenantId);
        body.Should().Contain("\"isPaused\":false");
    }

    [Fact]
    public async Task PostDunningResume_ShouldReturn403_WhenLackingBillingDunningManage()
    {
        // Reuse the established RBAC pattern: a non-platform-admin caller
        // hitting a "PlatformAdminOnly"-gated endpoint must surface as 403.
        await using var nonAdmin = new NonAdminAuthenticatedApiFactory();
        using var nonAdminClient = nonAdmin.CreateAuthenticatedClient();

        var response = await nonAdminClient.PostAsync(
            "/api/management/tenants/any-tenant/dunning/resume", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PostDunningResume_ShouldEmitAuditEntry_WhenInvoked()
    {
        const string tenantId = "dunning-resume-audit";
        await SeedPausedDunningRecordAsync(tenantId);

        var response = await _client.PostAsync(
            $"/api/management/tenants/{tenantId}/dunning/resume", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = _factory.Services.CreateScope();
        var auditStore = scope.ServiceProvider.GetRequiredService<IAuditStore>();
        var hits = await auditStore.SearchAsync(
            new TenantId(tenantId),
            new AuditQuery(Action: "billing.dunning.resumed", Page: 1, PageSize: 50),
            CancellationToken.None);

        hits.Items.Should().NotBeEmpty();
        var entry = hits.Items.First(e => e.TargetId == tenantId);
        entry.Category.Should().Be("billing");
        entry.Severity.Should().Be("info");
        entry.ActorType.Should().Be("user");
    }

    [Fact]
    public async Task PostDunningResume_ShouldClearDunningPausedFlag()
    {
        const string tenantId = "dunning-resume-flag";
        await SeedPausedDunningRecordAsync(tenantId);

        var response = await _client.PostAsync(
            $"/api/management/tenants/{tenantId}/dunning/resume", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = _factory.Services.CreateScope();
        var dunningStore = scope.ServiceProvider.GetRequiredService<IDunningStore>();
        var record = await dunningStore.GetActiveAsync(tenantId, CancellationToken.None);
        record.Should().NotBeNull();
        record!.IsPaused.Should().BeFalse();
    }

    private async Task SeedPausedDunningRecordAsync(string tenantId)
    {
        using var scope = _factory.Services.CreateScope();
        var dunningStore = scope.ServiceProvider.GetRequiredService<IDunningStore>();
        await dunningStore.UpsertAsync(new DunningRecord
        {
            DunningId = $"dunning-{tenantId}",
            TenantId = tenantId,
            InvoiceId = $"invoice-{tenantId}",
            StartedAt = DateTimeOffset.UtcNow.AddDays(-3),
            IsPaused = true,
            IsActive = true,
        }, CancellationToken.None);
    }
}
