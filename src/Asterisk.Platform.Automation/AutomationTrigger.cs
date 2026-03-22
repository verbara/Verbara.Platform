namespace Asterisk.Platform.Automation;

public enum AutomationTrigger
{
    ConversationCreated = 0,
    MessageReceived = 1,
    StateChanged = 2,
    ConversationAssigned = 3,
    ConversationClosed = 4,
    TimerElapsed = 5,
    SlaBreached = 6,
}
