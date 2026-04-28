using System.Text.Json;
using Asterisk.Platform.Api.Serialization;
using Asterisk.Platform.Core;
using Asterisk.Platform.Identity;

namespace Asterisk.Platform.Api.Services;

internal sealed class AuthEventService
{
    private readonly IAuthEventStore _store;
    private readonly AuthWriteQueue? _queue;

    public AuthEventService(IAuthEventStore store, AuthWriteQueue? queue = null)
    {
        _store = store;
        _queue = queue;
    }

    public async Task LogAsync(
        string tenantId,
        string? userId,
        string eventType,
        string? ipAddress,
        string? userAgent,
        Dictionary<string, string>? details,
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
            Details = details is not null
                ? JsonDocument.Parse(JsonSerializer.Serialize(details, ApiJsonContext.Default.DictionaryStringString))
                : null,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await _store.SaveAsync(authEvent, ct);
    }

    /// <summary>
    /// AHH Phase 2 — defer a success-path auth event to the
    /// <see cref="AuthWriteQueue"/>. Returns immediately. Callers MUST NOT
    /// use this for failure-path events (LoginFailure, Lockout, etc.) — those
    /// stay synchronous via <see cref="LogAsync"/> per the audit-completeness
    /// invariant in ADR-0011.
    /// </summary>
    /// <returns>
    /// <c>true</c> when the command was accepted by the queue; <c>false</c>
    /// when the bounded channel was full (drop counted via the
    /// <c>auth.write.dropped</c> meter). Most callers can safely ignore the
    /// return value — the meter + log line surface saturation to ops.
    /// </returns>
    public bool EnqueueLogSuccess(
        string tenantId,
        string userId,
        string eventType,
        string? ipAddress,
        string? userAgent)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(userId);
        ArgumentNullException.ThrowIfNull(eventType);

        if (_queue is null)
        {
            // Single-process tests / DI configurations without the queue
            // bootstrapped fall back to fire-and-forget sync log so behavior
            // stays observable. Production registers AuthWriteQueue.
            _ = LogAsync(tenantId, userId, eventType, ipAddress, userAgent, null, CancellationToken.None);
            return true;
        }

        return _queue.TryEnqueue(new LogSuccessEventCommand(tenantId, userId, eventType, ipAddress, userAgent));
    }

    public Task<PagedResult<AuthEvent>> ListByTenantAsync(
        string tenantId, int page, int pageSize, CancellationToken ct) =>
        _store.ListByTenantAsync(tenantId, page, pageSize, ct);

    public Task<PagedResult<AuthEvent>> ListByUserAsync(
        string tenantId, string userId, int page, int pageSize, CancellationToken ct) =>
        _store.ListByUserAsync(tenantId, userId, page, pageSize, ct);

    public Task<PagedResult<AuthEvent>> SearchAsync(
        string tenantId, AuthEventQuery query, CancellationToken ct) =>
        _store.SearchAsync(tenantId, query, ct);
}
