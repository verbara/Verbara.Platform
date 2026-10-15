namespace Verbara.Platform.Core.Webhooks;

/// <summary>
/// An exclusive claim on one outbound webhook delivery, taken atomically on the stored row before each POST
/// (webhook-delivery-runs-once, design D2 and D8; Platform/ADR-0043). Only the holder of <see cref="ClaimId"/> may
/// record the attempt's outcome or release the claim. Never serialized: claim data is not part of the delivery API.
/// </summary>
/// <param name="Delivery">The delivery as stored when it was claimed: the current row, never a caller's copy.</param>
/// <param name="ClaimId">The fencing id of this claim (32 hex characters). It is not a credential.</param>
/// <param name="Reclaimed">
/// <see langword="true"/> when this claim took over an earlier claim whose lease had expired, that is, an attempt
/// that never recorded its outcome.
/// </param>
public sealed record WebhookDeliveryClaim(WebhookDelivery Delivery, string ClaimId, bool Reclaimed);
