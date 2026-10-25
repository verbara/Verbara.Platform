namespace Verbara.Platform.Typification;

/// <summary>
/// The disposition a stored typification owes the dialer (dialer-honours-dnc-callbacks-and-agent-dispositions, design
/// D16): one row per typified campaign call, written in the same transaction as its
/// <see cref="TypificationSubmission"/> and handed to the dialer under a claim with a lease, by the request and then by
/// any replica's dispatcher, until the dialer answers. It carries everything the dialer needs, so nothing is looked
/// up again at hand-off time.
/// </summary>
public sealed record DialerDispositionDispatch
{
    /// <summary>The Conversation's tenant, which is the campaign's tenant.</summary>
    public required string TenantId { get; init; }

    /// <summary>The typified Conversation; with <see cref="TenantId"/>, the row's key.</summary>
    public required string ConversationId { get; init; }

    public required long CampaignId { get; init; }

    /// <summary>The originate's action id (<c>DIALER_ACTION_ID</c>), the key the dialer applies a disposition by.</summary>
    public required string ActionId { get; init; }

    /// <summary>The dialer contact the call was placed for (<c>DIALER_CONTACT_ID</c>), when it was read.</summary>
    public long? DialerContactId { get; init; }

    /// <summary>The matched active disposition code of the campaign.</summary>
    public required long DispositionCodeId { get; init; }

    /// <summary>The matched code's text, for the <c>campaign.disposition_submitted</c> event.</summary>
    public required string DispositionCode { get; init; }

    /// <summary>The agent's notes, written on the call's attempt.</summary>
    public string? Comment { get; init; }

    /// <summary>The parsed <c>callback_date</c>, when the typification carried one.</summary>
    public DateTimeOffset? CallbackAt { get; init; }

    /// <summary>The typification leaf requests a callback, whatever the code's own flag.</summary>
    public bool RequestsCallback { get; init; }

    /// <summary>The agent who typified the call.</summary>
    public string? AgentId { get; init; }

    /// <summary>How many claims the row has had: the request's is the first, each worker claim adds one.</summary>
    public int Attempts { get; init; }

    /// <summary>When the row is next due for a worker, while it has no result.</summary>
    public DateTimeOffset NextAttemptAt { get; init; }

    /// <summary>The fencing id of the claim that holds the row, if any (32 hex characters).</summary>
    public string? ClaimId { get; init; }

    /// <summary>The claim's lease: once it has passed, any dispatcher may claim the row again.</summary>
    public DateTimeOffset? ClaimExpiresAt { get; init; }

    /// <summary>The dialer's answer that completed the row (an <c>AgentDispositionResult</c> name), or null while pending.</summary>
    public string? Result { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>When the completing dispatcher announced the disposition, or null when it announced nothing.</summary>
    public DateTimeOffset? PublishedAt { get; init; }

    /// <summary>The last hand-off failure, kept for the operator.</summary>
    public string? LastError { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}
