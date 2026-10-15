namespace Verbara.Platform.Core.Webhooks;

public interface IWebhookDeliveryStore
{
    Task SaveAsync(WebhookDelivery delivery, CancellationToken ct);
    Task<WebhookDelivery?> GetByIdAsync(string deliveryId, CancellationToken ct);
    Task<IReadOnlyList<WebhookDelivery>> ListPendingRetriesAsync(
        DateTimeOffset now, int batchSize, CancellationToken ct);
    Task<PagedResult<WebhookDelivery>> ListBySubscriptionAsync(
        string subscriptionId, int page, int pageSize, CancellationToken ct);
    Task<PagedResult<WebhookDelivery>> ListDeadLetterAsync(
        string tenantId, int page, int pageSize, CancellationToken ct);
    Task UpdateAsync(WebhookDelivery delivery, CancellationToken ct);
    Task DeleteBySubscriptionAsync(string subscriptionId, CancellationToken ct);

    /// <summary>
    /// Claims one delivery by id, if it is claimable: <c>Pending</c>, due at <paramref name="now"/>, and with no
    /// claim whose lease is still running. The claim lasts until <paramref name="expiresAt"/>. A delivery that is
    /// being claimed by another caller at this instant is skipped, not waited for (design D2).
    /// </summary>
    /// <returns>The claim, or <see langword="null"/> when the delivery is not claimable.</returns>
    Task<WebhookDeliveryClaim?> TryClaimAsync(
        string deliveryId, DateTimeOffset now, DateTimeOffset expiresAt, CancellationToken ct);

    /// <summary>
    /// Claims the claimable delivery that has been due the longest (<c>Pending</c>, due at <paramref name="now"/>,
    /// and with no claim whose lease is still running). The claim lasts until <paramref name="expiresAt"/>. Rows
    /// that another caller is claiming at this instant are skipped, so concurrent callers never claim the same one.
    /// </summary>
    /// <returns>The claim, or <see langword="null"/> when nothing is claimable.</returns>
    Task<WebhookDeliveryClaim?> ClaimNextDueAsync(
        DateTimeOffset now, DateTimeOffset expiresAt, CancellationToken ct);

    /// <summary>
    /// Records an attempt's outcome (status, attempts, next retry, last response code, last error and delivered
    /// time, from <paramref name="outcome"/>) and clears the claim, only while <paramref name="claimId"/> is still
    /// the delivery's current claim.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when another attempt has taken the claim over (or it was already cleared): nothing
    /// was written.
    /// </returns>
    Task<bool> CompleteAttemptAsync(WebhookDelivery outcome, string claimId, CancellationToken ct);

    /// <summary>
    /// Clears the claim without recording an attempt, and makes the delivery due again at
    /// <paramref name="nextRetryAt"/>, only while <paramref name="claimId"/> is still the delivery's current claim.
    /// Every other column, <c>attempts</c> included, is left as it is.
    /// </summary>
    /// <returns><see langword="false"/> when <paramref name="claimId"/> is not the current claim: nothing was written.</returns>
    Task<bool> ReleaseClaimAsync(
        string deliveryId, string claimId, DateTimeOffset nextRetryAt, CancellationToken ct);
}
