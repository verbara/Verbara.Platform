using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Sdk.Pro.MultiTenant;

namespace Verbara.Platform.Api.Endpoints.Mfa;

/// <summary>
/// Default <see cref="IMfaAdminService"/> implementation that walks the tenant
/// store + per-tenant <see cref="IUserStore"/> + revokes refresh tokens via
/// <see cref="IRefreshTokenStore"/>. Designed for AOT (no reflection).
/// </summary>
internal sealed class MfaAdminService : IMfaAdminService
{
    private readonly ITenantStore _tenantStore;
    private readonly IUserStore _userStore;
    private readonly IRefreshTokenStore _refreshTokenStore;
    private readonly TimeProvider _clock;

    public MfaAdminService(
        ITenantStore tenantStore,
        IUserStore userStore,
        IRefreshTokenStore refreshTokenStore,
        TimeProvider? clock = null)
    {
        _tenantStore = tenantStore;
        _userStore = userStore;
        _refreshTokenStore = refreshTokenStore;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<PagedResult<MfaUserSummary>> ListAsync(
        MfaUserListFilter filter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var page = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, 200);

        var tenants = await ResolveTenantsAsync(filter.TenantId, ct);
        if (tenants.Count == 0)
            return PagedResult<MfaUserSummary>.Empty(page, pageSize);

        var now = _clock.GetUtcNow();
        var statusFilter = NormalizeStatus(filter.Status);

        // Stream users tenant-by-tenant — keeps memory bounded for the common
        // case (single-tenant filter) and remains correct for multi-tenant.
        var summaries = new List<MfaUserSummary>(capacity: pageSize * tenants.Count);
        foreach (var tenant in tenants)
        {
            var tenantUsers = await _userStore.ListAsync(
                new TenantId(tenant.TenantId),
                new PagedQuery(1, 1000),
                ct);

            foreach (var user in tenantUsers.Items)
            {
                var status = ResolveStatus(user, now);
                if (statusFilter is not null &&
                    !string.Equals(statusFilter, status, StringComparison.Ordinal))
                {
                    continue;
                }

                summaries.Add(new MfaUserSummary(
                    UserId: user.UserId.Value,
                    Username: user.Email,
                    TenantId: tenant.TenantId,
                    TenantName: tenant.Name,
                    Status: status,
                    EnrolledAt: user.MfaConfirmedAt,
                    LastUsedAt: user.LastLoginAt));
            }
        }

        var totalCount = summaries.Count;
        var pageItems = summaries
            .OrderBy(s => s.TenantId, StringComparer.Ordinal)
            .ThenBy(s => s.Username, StringComparer.OrdinalIgnoreCase)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        return new PagedResult<MfaUserSummary>(pageItems, totalCount, page, pageSize);
    }

    public async Task<bool> ResetMfaAsync(
        TenantId tenantId, EntityId userId, CancellationToken ct)
    {
        var user = await _userStore.GetByIdAsync(tenantId, userId, ct);
        if (user is null)
            return false;

        user.MfaEnabled = false;
        user.MfaSecret = null;
        user.MfaRecoveryCodes = null;
        user.MfaConfirmedAt = null;
        user.LockedUntil = null;
        user.FailedLoginAttempts = 0;
        user.UpdatedAt = _clock.GetUtcNow();

        await _userStore.SaveAsync(user, ct);
        return true;
    }

    public async Task<int> RevokeSessionsAsync(
        TenantId tenantId, EntityId userId, CancellationToken ct)
    {
        // Verify user exists in this tenant before broadcasting a revoke; the
        // sentinel -1 propagates to the endpoint as 404.
        var user = await _userStore.GetByIdAsync(tenantId, userId, ct);
        if (user is null)
            return -1;

        var active = await _refreshTokenStore.GetActiveByUserAsync(
            tenantId.Value, userId.Value, ct);
        await _refreshTokenStore.RevokeAllForUserAsync(
            tenantId.Value, userId.Value, _clock.GetUtcNow(), ct);

        return active.Count;
    }

    private async Task<IReadOnlyList<Tenant>> ResolveTenantsAsync(
        string? tenantIdFilter, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(tenantIdFilter))
        {
            var tenant = await _tenantStore.GetAsync(tenantIdFilter, ct);
            return tenant is null ? Array.Empty<Tenant>() : [tenant];
        }

        return await _tenantStore.GetAllActiveAsync(ct);
    }

    private static string ResolveStatus(User user, DateTimeOffset now)
    {
        if (user.IsLockedOut(now))
            return "locked";
        return user.MfaEnabled ? "enrolled" : "not-enrolled";
    }

    private static string? NormalizeStatus(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var trimmed = raw.Trim().ToLowerInvariant();
        return trimmed switch
        {
            "enrolled" or "not-enrolled" or "locked" => trimmed,
            _ => null,
        };
    }
}
