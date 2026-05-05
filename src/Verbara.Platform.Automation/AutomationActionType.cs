namespace Verbara.Platform.Automation;

public enum AutomationActionType
{
    AssignToQueue = 0,
    SendMessage = 1,
    SetPriority = 2,
    AddTag = 3,
    SetMetadata = 4,
    TransferToBot = 5,
    EscalateToSupervisor = 6,
    CloseConversation = 7,
    ScheduleTimer = 8,
    SendSurvey = 9,
    Webhook = 10,
}
