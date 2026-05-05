using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Platform.Core.Impersonation;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Verbara.Platform.Api.Tests.Impersonation;

/// <summary>
/// R5.2 PB.2 — admin session-management endpoint integration coverage.
/// Pairs the host-tenant + Management API key seeded in
/// <see cref="PlatformAdminApiFactory"/> with the new
/// <c>/management/impersonation/sessions/*</c> endpoints to exercise auth,
/// listing, manual revoke + audit, and history flows end-to-end.
/// </summary>
public sealed class ImpersonationAdminEndpointsTests : IClassFixture<PlatformAdminApiFactory>
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly PlatformAdminApiFactory _factory;
    private readonly HttpClient _client;

    public ImpersonationAdminEndpointsTests(PlatformAdminApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreatePlatformAdminClient();
    }

    [Fact]
    public async Task GetActiveSessions_ShouldReturnPagedList_WhenAuthorized()
    {
        var seeded1 = await SeedActiveSessionAsync("active-1", "tenant-acme");
        var seeded2 = await SeedActiveSessionAsync("active-2", "tenant-acme");

        var response = await _client.GetAsync(
            "/api/v1/management/impersonation/sessions/active?pageSize=50");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var paged = await response.Content.ReadFromJsonAsync<PagedDto<ImpersonationSessionWire>>(s_jsonOptions);
        paged.Should().NotBeNull();
        paged!.Items.Should().Contain(s => s.Id == seeded1.Id);
        paged.Items.Should().Contain(s => s.Id == seeded2.Id);
    }

    [Fact]
    public async Task GetActiveSessions_ShouldReturn403_WhenMissingPermission()
    {
        await using var nonAdmin = new NonAdminAuthenticatedApiFactory();
        using var nonAdminClient = nonAdmin.CreateAuthenticatedClient();

        var response = await nonAdminClient.GetAsync(
            "/api/v1/management/impersonation/sessions/active");

        // PlatformAdminAuthorizationHandler refuses (Agent role + non-host
        // tenant), surfacing as 403 from the authorization pipeline.
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task RevokeSession_ShouldReturn204AndAuditEntry_WhenAuthorized()
    {
        var session = await SeedActiveSessionAsync("revoke-target", "tenant-acme");

        var revokeBody = new { reason = "support_workflow_complete" };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/management/impersonation/sessions/{session.Id}/revoke", revokeBody);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Session is now closed in the store.
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IImpersonationSessionStore>();
        var refreshed = await store.GetAsync(session.Id, CancellationToken.None);
        refreshed.Should().NotBeNull();
        refreshed!.Status.Should().Be(ImpersonationSessionStatus.ManuallyRevoked);
        refreshed.CloseReason.Should().Be("support_workflow_complete");

        // Audit entry written with the canonical action name.
        var audit = scope.ServiceProvider.GetRequiredService<IAuditStore>();
        var hits = await audit.SearchAsync(
            new TenantId(PlatformAdminApiFactory.HostTenantId),
            new AuditQuery(Action: "impersonation.session.revoked", Page: 1, PageSize: 50),
            CancellationToken.None);
        hits.Items.Should().Contain(e => e.TargetId == session.Id);
    }

    [Fact]
    public async Task GetHistory_ShouldReturnPagedHistoryEntries_WhenAuthorized()
    {
        // Closed sessions fall into the history view; an active one stays out.
        var closed = await SeedClosedSessionAsync("history-1", "tenant-acme",
            ImpersonationSessionStatus.Completed, "completed");
        var stillActive = await SeedActiveSessionAsync("history-active", "tenant-acme");

        var response = await _client.GetAsync(
            "/api/v1/management/impersonation/sessions/history?pageSize=50");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var paged = await response.Content.ReadFromJsonAsync<PagedDto<ImpersonationSessionWire>>(s_jsonOptions);
        paged.Should().NotBeNull();
        paged!.Items.Should().Contain(s => s.Id == closed.Id);
        paged.Items.Should().NotContain(s => s.Id == stillActive.Id);
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private async Task<ImpersonationSession> SeedActiveSessionAsync(string id, string targetTenant)
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IImpersonationSessionStore>();
        var session = new ImpersonationSession
        {
            Id = id,
            ActorUserId = PlatformAdminApiFactory.TestPlatformAdminUserId,
            ActorTenantId = PlatformAdminApiFactory.HostTenantId,
            TargetTenantId = targetTenant,
            Reason = "support",
            ReadOnly = false,
            StartedAt = DateTimeOffset.UtcNow,
        };
        await store.AddAsync(session, CancellationToken.None);
        return session;
    }

    private async Task<ImpersonationSession> SeedClosedSessionAsync(
        string id, string targetTenant, ImpersonationSessionStatus status, string reason)
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IImpersonationSessionStore>();
        var session = new ImpersonationSession
        {
            Id = id,
            ActorUserId = PlatformAdminApiFactory.TestPlatformAdminUserId,
            ActorTenantId = PlatformAdminApiFactory.HostTenantId,
            TargetTenantId = targetTenant,
            ReadOnly = false,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-30),
            Status = status,
            CloseReason = reason,
            EndedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        };
        await store.AddAsync(session, CancellationToken.None);
        return session;
    }

    private sealed record PagedDto<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize);

    private sealed record ImpersonationSessionWire(
        string Id,
        string ActorUserId,
        string ActorTenantId,
        string? TargetUserId,
        string TargetTenantId,
        string? Reason,
        bool ReadOnly,
        DateTimeOffset StartedAt,
        DateTimeOffset? EndedAt,
        string Status,
        string? CloseReason,
        int? TimeRemainingSeconds,
        DateTimeOffset? ExpiresAt);
}
