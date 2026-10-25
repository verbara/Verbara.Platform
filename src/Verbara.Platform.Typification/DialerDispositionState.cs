namespace Verbara.Platform.Typification;

/// <summary>
/// What the dialer did with the disposition a typification hands it, as the typify response reports it in
/// <see cref="TypificationSubmission.DialerDisposition"/> (dialer-honours-dnc-callbacks-and-agent-dispositions, design
/// D16, owner Q8). It is the state of that request's hand-off when the response is written; no store keeps it.
/// </summary>
public enum DialerDispositionState
{
    /// <summary>
    /// Nothing is handed to the dialer: the Conversation is not a campaign call (no campaign id or no dialer action
    /// id), the leaf has no dialer code, or its code is not an active code of the campaign.
    /// </summary>
    None,

    /// <summary>The dialer applied the disposition and its effect (retry, callback or do-not-call).</summary>
    Applied,

    /// <summary>
    /// The dialer had already accepted a disposition for this call's action id, applied or still awaiting the call's
    /// outcome; nothing was applied a second time.
    /// </summary>
    AlreadyApplied,

    /// <summary>
    /// The dialer accepted the disposition and applies its effect when the call's outcome is recorded (the normal
    /// result of a typification sent while the call is still up).
    /// </summary>
    AwaitingOutcome,

    /// <summary>The dialer knows no call of the campaign for the action id; nothing was applied and a warning was logged.</summary>
    UnknownCall,

    /// <summary>
    /// The hand-off was not confirmed in this request; the dispatcher hands it over again until the dialer answers.
    /// Not confirmed is not "not applied": the retry completes with <see cref="AlreadyApplied"/> when the dialer had
    /// in fact applied it.
    /// </summary>
    Pending,
}
