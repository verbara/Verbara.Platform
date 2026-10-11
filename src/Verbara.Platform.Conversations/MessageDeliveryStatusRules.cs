namespace Verbara.Platform.Conversations;

/// <summary>
/// The one ordering rule for <see cref="MessageDeliveryStatus"/> (whatsapp-works-for-real design D9):
/// <c>Pending &lt; Sent &lt; Delivered &lt; Read</c>, and <c>Failed</c> is terminal. A status only moves
/// forward, so a replayed or late provider callback (a <c>sent</c> after <c>read</c>) never undoes a later
/// state. <c>Failed</c> is reachable only before the message was delivered (from <c>Pending</c> or
/// <c>Sent</c>). The Postgres message store mirrors this rule in SQL; keep the two in step.
/// </summary>
public static class MessageDeliveryStatusRules
{
    /// <summary>Position on the forward path; <c>Failed</c> is off the path and has no rank.</summary>
    public static int Rank(MessageDeliveryStatus status) => status switch
    {
        MessageDeliveryStatus.Pending => 0,
        MessageDeliveryStatus.Sent => 1,
        MessageDeliveryStatus.Delivered => 2,
        MessageDeliveryStatus.Read => 3,
        _ => -1,
    };

    /// <summary>True when a message in <paramref name="current"/> may move to <paramref name="next"/>.</summary>
    public static bool CanAdvance(MessageDeliveryStatus current, MessageDeliveryStatus next)
    {
        if (current == MessageDeliveryStatus.Failed)
            return false;

        if (next == MessageDeliveryStatus.Failed)
            return current is MessageDeliveryStatus.Pending or MessageDeliveryStatus.Sent;

        return Rank(next) > Rank(current);
    }
}
