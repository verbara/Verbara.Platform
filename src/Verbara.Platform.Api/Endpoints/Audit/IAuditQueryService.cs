using System.Runtime.CompilerServices;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;

namespace Verbara.Platform.Api.Endpoints.Audit;

/// <summary>
/// Read-side facade in front of <see cref="IAuditStore"/> used by the R5.2 PB.1
/// audit log viewer + export endpoints. Wraps store calls so the endpoints stay
/// trivially testable and so future caching / read-replica routing only need to
/// change one composition root.
/// </summary>
public interface IAuditQueryService
{
    /// <summary>Returns a paged set of audit events scoped to the supplied tenant.</summary>
    Task<PagedResult<AuditEventDto>> QueryAsync(
        TenantId tenantId,
        AuditQueryFilter filter,
        CancellationToken ct);

    /// <summary>Streams matching events without materialising the full result set.</summary>
    IAsyncEnumerable<AuditEventDto> StreamAsync(
        TenantId tenantId,
        AuditQueryFilter filter,
        CancellationToken ct);
}

/// <summary>
/// Public filter contract for <see cref="IAuditQueryService"/>. Lives at the
/// API boundary instead of <c>Verbara.Platform.Audit</c> so the endpoint can
/// model presentation-friendly fields (action prefix, actor substring) without
/// expanding the storage abstraction further.
/// </summary>
public sealed record AuditQueryFilter
{
    public string? ActionPrefix { get; init; }

    /// <summary>User ID OR substring of the username/actor field.</summary>
    public string? Actor { get; init; }

    /// <summary>Target identifier (agent / user / tenant / etc.).</summary>
    public string? Target { get; init; }

    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }

    /// <summary>Optional cross-tenant scope (PlatformAdmin only — endpoints enforce).</summary>
    public string? TenantId { get; init; }

    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;

    public AuditQuery ToAuditQuery() => new(
        From: From,
        To: To,
        Page: Page,
        PageSize: PageSize,
        ActionPrefix: ActionPrefix,
        ActorSearch: Actor,
        TargetId: Target);
}

/// <summary>Default implementation backed by <see cref="IAuditStore"/>.</summary>
internal sealed class DefaultAuditQueryService : IAuditQueryService
{
    private readonly IAuditStore _store;

    public DefaultAuditQueryService(IAuditStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public async Task<PagedResult<AuditEventDto>> QueryAsync(
        TenantId tenantId, AuditQueryFilter filter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var page = await _store.SearchAsync(tenantId, filter.ToAuditQuery(), ct);
        var dtos = page.Items.Select(MapToDto).ToList();
        return new PagedResult<AuditEventDto>(dtos, page.TotalCount, page.Page, page.PageSize);
    }

    public async IAsyncEnumerable<AuditEventDto> StreamAsync(
        TenantId tenantId,
        AuditQueryFilter filter,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);

        await foreach (var entry in _store.StreamAsync(tenantId, filter.ToAuditQuery(), ct).ConfigureAwait(false))
        {
            yield return MapToDto(entry);
        }
    }

    internal static AuditEventDto MapToDto(AuditEntry entry) => new()
    {
        EntryId = entry.EntryId.Value,
        TenantId = entry.TenantId.Value,
        Action = entry.Action,
        Category = entry.Category,
        Severity = entry.Severity,
        ActorId = entry.ActorId,
        ActorType = entry.ActorType,
        TargetId = entry.TargetId,
        TargetType = entry.TargetType,
        CorrelationId = entry.CorrelationId?.ToString(),
        ImpersonatorId = entry.ImpersonatorId,
        OccurredAt = entry.OccurredAt,
        Status = DeriveStatus(entry.Severity),
        Before = entry.Changes?.Before,
        After = entry.Changes?.After,
        Metadata = entry.Metadata,
    };

    private static string DeriveStatus(string severity) => severity switch
    {
        "critical" => "error",
        "warning" => "warning",
        _ => "ok",
    };
}
