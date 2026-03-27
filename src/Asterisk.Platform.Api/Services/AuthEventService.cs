using System.Text.Json;
using Asterisk.Platform.Core;
using Asterisk.Platform.Identity;

namespace Asterisk.Platform.Api.Services;

internal sealed class AuthEventService
{
    private readonly IAuthEventStore _store;

    public AuthEventService(IAuthEventStore store) => _store = store;

    public async Task LogAsync(
        string tenantId,
        string? userId,
        string eventType,
        string? ipAddress,
        string? userAgent,
        object? details,
        CancellationToken ct)
    {
        var authEvent = new AuthEvent
        {
            EventId = Guid.NewGuid().ToString("N"),
            TenantId = tenantId,
            UserId = userId,
            EventType = eventType,
            IpAddress = ipAddress,
            UserAgent = userAgent,
            Details = details is not null ? JsonSerializer.SerializeToDocument(details) : null,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await _store.SaveAsync(authEvent, ct);
    }

    public Task<PagedResult<AuthEvent>> ListByTenantAsync(
        string tenantId, int page, int pageSize, CancellationToken ct) =>
        _store.ListByTenantAsync(tenantId, page, pageSize, ct);

    public Task<PagedResult<AuthEvent>> ListByUserAsync(
        string tenantId, string userId, int page, int pageSize, CancellationToken ct) =>
        _store.ListByUserAsync(tenantId, userId, page, pageSize, ct);
}
