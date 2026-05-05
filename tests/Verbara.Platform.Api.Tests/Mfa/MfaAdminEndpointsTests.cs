using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Verbara.Platform.Api.Endpoints.Mfa;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Sdk.Pro.MultiTenant;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Verbara.Platform.Api.Tests.Mfa;

/// <summary>
/// R5.2 Phase A — PA.1 MFA admin surface integration coverage. Pairs the
/// host-tenant + Management API key seeded in <see cref="PlatformAdminApiFactory"/>
/// with the new <c>/management/mfa/*</c> endpoints to exercise auth, listing,
/// reset, and revoke flows end-to-end.
/// </summary>
public sealed class MfaAdminEndpointsTests : IClassFixture<PlatformAdminApiFactory>
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly PlatformAdminApiFactory _factory;
    private readonly HttpClient _client;

    public MfaAdminEndpointsTests(PlatformAdminApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreatePlatformAdminClient();
    }

    [Fact]
    public async Task ListUsers_ShouldReturnPagedResult_WhenAuthorized()
    {
        await SeedUserAsync("mfa-list-1", "alice@list.test", mfaEnabled: true);
        await SeedUserAsync("mfa-list-2", "bob@list.test", mfaEnabled: false);

        var response = await _client.GetAsync("/api/management/mfa/users?pageSize=50");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var paged = await response.Content.ReadFromJsonAsync<PagedDto<MfaUserSummary>>(s_jsonOptions);
        paged.Should().NotBeNull();
        paged!.Items.Should().Contain(u => u.UserId == "mfa-list-1");
        paged.Items.Should().Contain(u => u.UserId == "mfa-list-2");
    }

    [Fact]
    public async Task ListUsers_ShouldReturn403_WhenMissingPermission()
    {
        await using var nonAdmin = new NonAdminAuthenticatedApiFactory();
        using var nonAdminClient = nonAdmin.CreateAuthenticatedClient();

        var response = await nonAdminClient.GetAsync("/api/management/mfa/users");

        // PlatformAdminAuthorizationHandler refuses (Agent role + non-host
        // tenant), surfacing as 403 from the authorization pipeline.
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ListUsers_ShouldFilterByStatus_WhenStatusProvided()
    {
        await SeedUserAsync("mfa-filter-enrolled", "enrolled@filter.test", mfaEnabled: true);
        await SeedUserAsync("mfa-filter-not", "notenrolled@filter.test", mfaEnabled: false);

        var enrolled = await _client.GetAsync("/api/management/mfa/users?status=enrolled&pageSize=200");
        enrolled.StatusCode.Should().Be(HttpStatusCode.OK);
        var enrolledPage = await enrolled.Content.ReadFromJsonAsync<PagedDto<MfaUserSummary>>(s_jsonOptions);
        enrolledPage!.Items.Should().Contain(u => u.UserId == "mfa-filter-enrolled");
        enrolledPage.Items.Should().NotContain(u => u.UserId == "mfa-filter-not");

        var notEnrolled = await _client.GetAsync("/api/management/mfa/users?status=not-enrolled&pageSize=200");
        notEnrolled.StatusCode.Should().Be(HttpStatusCode.OK);
        var notEnrolledPage = await notEnrolled.Content.ReadFromJsonAsync<PagedDto<MfaUserSummary>>(s_jsonOptions);
        notEnrolledPage!.Items.Should().Contain(u => u.UserId == "mfa-filter-not");
        notEnrolledPage.Items.Should().NotContain(u => u.UserId == "mfa-filter-enrolled");
    }

    [Fact]
    public async Task ResetMfa_ShouldReturn204AndAuditEntry_WhenAuthorized()
    {
        await SeedUserAsync("mfa-reset-target", "reset@target.test", mfaEnabled: true);

        var response = await _client.PostAsync(
            "/api/management/mfa/users/mfa-reset-target/reset", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Verify the user lost MFA state.
        using var scope = _factory.Services.CreateScope();
        var userStore = scope.ServiceProvider.GetRequiredService<IUserStore>();
        var refreshed = await userStore.GetByIdAsync(
            new TenantId(PlatformAdminApiFactory.HostTenantId),
            EntityId.From("mfa-reset-target"),
            CancellationToken.None);
        refreshed.Should().NotBeNull();
        refreshed!.MfaEnabled.Should().BeFalse();
        refreshed.MfaSecret.Should().BeNull();

        // Audit entry written with the canonical action name.
        var audit = scope.ServiceProvider.GetRequiredService<IAuditStore>();
        var hits = await audit.SearchAsync(
            new TenantId(PlatformAdminApiFactory.HostTenantId),
            new AuditQuery(Action: "mfa.admin.reset", Page: 1, PageSize: 50),
            CancellationToken.None);
        hits.Items.Should().Contain(e => e.TargetId == "mfa-reset-target");
    }

    [Fact]
    public async Task RevokeSessions_ShouldReturn204AndAuditEntry_WhenAuthorized()
    {
        await SeedUserAsync("mfa-revoke-target", "revoke@target.test", mfaEnabled: true);

        var response = await _client.PostAsync(
            "/api/management/mfa/users/mfa-revoke-target/sessions/revoke", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = _factory.Services.CreateScope();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditStore>();
        var hits = await audit.SearchAsync(
            new TenantId(PlatformAdminApiFactory.HostTenantId),
            new AuditQuery(Action: "mfa.admin.sessions_revoked", Page: 1, PageSize: 50),
            CancellationToken.None);
        hits.Items.Should().Contain(e => e.TargetId == "mfa-revoke-target");
    }

    private async Task SeedUserAsync(string userId, string email, bool mfaEnabled)
    {
        using var scope = _factory.Services.CreateScope();
        var userStore = scope.ServiceProvider.GetRequiredService<IUserStore>();
        await userStore.SaveAsync(new User
        {
            UserId = EntityId.From(userId),
            TenantId = new TenantId(PlatformAdminApiFactory.HostTenantId),
            Email = email,
            DisplayName = email,
            Role = UserRole.Agent,
            Status = UserStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            MfaEnabled = mfaEnabled,
            MfaSecret = mfaEnabled ? "JBSWY3DPEHPK3PXP" : null,
            MfaConfirmedAt = mfaEnabled ? DateTimeOffset.UtcNow : null,
        }, CancellationToken.None);
    }

    private sealed record PagedDto<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize);
}
