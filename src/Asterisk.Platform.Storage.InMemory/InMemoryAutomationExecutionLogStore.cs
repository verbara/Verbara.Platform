using System.Collections.Concurrent;
using Asterisk.Platform.Core;
using Asterisk.Platform.Automation;

namespace Asterisk.Platform.Storage.InMemory;

internal sealed class InMemoryAutomationExecutionLogStore : IAutomationExecutionLogStore
{
    private readonly ConcurrentDictionary<string, AutomationExecutionLog> _items = new();

    public Task SaveAsync(AutomationExecutionLog log, CancellationToken ct)
    {
        _items[log.LogId.Value] = log;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AutomationExecutionLog>> GetByConversationAsync(TenantId tenantId, EntityId conversationId, CancellationToken ct)
    {
        IReadOnlyList<AutomationExecutionLog> result = _items.Values
            .Where(l => l.TenantId == tenantId && l.ConversationId == conversationId)
            .OrderByDescending(l => l.ExecutedAt)
            .ToList();

        return Task.FromResult(result);
    }
}
