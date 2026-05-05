namespace Verbara.Platform.Conversations;

public enum ConversationState
{
    Queued = 0,
    Offered = 1,
    Active = 10,
    OnHold = 11,
    Consulting = 12,
    WrapUp = 20,
    WaitingForCustomer = 30,
    Snoozed = 31,
    Resolved = 40,
    Escalated = 41,
    Closed = 50,
    Abandoned = 51,
    Merged = 52,
    Spam = 53,
}
