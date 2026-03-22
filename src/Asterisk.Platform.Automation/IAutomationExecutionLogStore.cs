using Asterisk.Platform.Core;

namespace Asterisk.Platform.Automation;

public interface IAutomationExecutionLogStore
{
    Task SaveAsync(AutomationExecutionLog log, CancellationToken ct);
    Task<IReadOnlyList<AutomationExecutionLog>> GetByConversationAsync(TenantId tenantId, EntityId conversationId, CancellationToken ct);
}
