using System.Diagnostics.CodeAnalysis;
using Verbara.Platform.Core;

namespace Verbara.Platform.Conversations;

[SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Domain term")]
public sealed class Case : ITenantScoped, IAuditable
{
    public required EntityId CaseId { get; init; }
    public required TenantId TenantId { get; init; }
    public required string CaseNumber { get; init; }
    public required string Subject { get; set; }
    public required CasePriority Priority { get; set; }
    public required CaseStatus Status { get; set; }
    public required EntityId ContactId { get; init; }
    public EntityId? AssignedAgentId { get; set; }
    public EntityId? SlaPolicyId { get; set; }

    private readonly List<EntityId> _conversationIds = [];
    public IReadOnlyList<EntityId> ConversationIds => _conversationIds;

    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? CreatedBy { get; init; }
    public string? UpdatedBy { get; set; }

    public void AddConversation(EntityId conversationId)
    {
        if (!_conversationIds.Contains(conversationId))
            _conversationIds.Add(conversationId);
    }
}
