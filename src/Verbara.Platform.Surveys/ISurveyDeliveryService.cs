using Verbara.Platform.Core;

namespace Verbara.Platform.Surveys;

/// <summary>Sends a survey to a contact via their active conversation channel.</summary>
public interface ISurveyDeliveryService
{
    Task DeliverAsync(Survey survey, EntityId conversationId, TenantId tenantId, EntityId senderId, CancellationToken ct);
}
