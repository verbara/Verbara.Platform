using Verbara.Platform.Core;

namespace Verbara.Platform.Typification.Ai;

/// <summary>
/// Per-tenant daily LLM token budget for AI typification classification (E3 — fail-closed
/// cost control). Tracks how many LLM tokens a tenant has consumed in the current UTC day and
/// answers whether a configured daily budget has been reached.
/// </summary>
/// <remarks>
/// The suggestion endpoint consults <see cref="IsOverBudgetAsync"/> BEFORE calling the LLM and
/// degrades to the empty suggestion (no LLM call) when the tenant is over budget, then records
/// the call's token usage via <see cref="RecordUsageAsync"/> after a successful classification.
/// The default <see cref="InMemoryTypificationTokenBudget"/> is an in-process accumulator
/// (accurate for a single instance; a multi-instance deployment would back this with Redis —
/// a future enhancement).
/// <para>
/// <b>Redis-seam decision point (fail direction):</b> a future Redis-backed implementation MUST
/// decide its own fail direction when the budget store is unavailable. The suggestion endpoint
/// currently lets a budget-store exception propagate (→ HTTP 500); the in-memory implementation
/// cannot throw, so no endpoint change exists today. A Redis implementation should catch the outage
/// <i>internally</i> and choose either <b>fail-open</b> (allow the call, uncapped during the outage)
/// or <b>fail-closed</b> (degrade to the empty suggestion) — that policy decision belongs with the
/// Redis implementation, not the endpoint.
/// </para>
/// </remarks>
public interface ITypificationTokenBudget
{
    /// <summary>
    /// Returns <see langword="true"/> when the tenant has already consumed at least
    /// <paramref name="dailyBudget"/> LLM tokens in the current UTC day (boundary inclusive —
    /// reaching the budget counts as over). Fail-closed: when over budget the caller must NOT
    /// call the LLM.
    /// </summary>
    Task<bool> IsOverBudgetAsync(TenantId tenantId, long dailyBudget, CancellationToken ct);

    /// <summary>
    /// Adds <paramref name="tokens"/> to the tenant's running token sum for the current UTC day.
    /// Non-positive values are ignored.
    /// </summary>
    Task RecordUsageAsync(TenantId tenantId, long tokens, CancellationToken ct);
}
