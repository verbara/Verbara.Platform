using Verbara.Platform.Audit;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Surveys;
using Verbara.Sdk.Pro.CsatRunner.Contracts;
using Verbara.Sdk.Pro.CsatRunner.Domain;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// Platform implementation of the Pro-defined <see cref="ICsatVoiceCaptureSink"/> seam
/// (csat-completion, Platform/ADR-0020, verbara-meta/ADR-0005 open-core boundary). The Pro voice CSAT
/// adapter collects the caller's 1-5 DTMF rating in-process (there is no inbound webhook for voice) and
/// hands the normalized <see cref="CsatCapture"/> here; this adapter lands it on the SAME Platform
/// Surveys capture path the digital HTTP endpoints reach — persist a <see cref="SurveyResponse"/>
/// (channel <c>voice</c>, <c>Comment</c> null, <see cref="SurveyResponse.CallId"/> set from the
/// correlated conversation), publish a <see cref="CsatResponseRecordedEvent"/> so the supervisor push
/// fires, and write a <c>csat</c>-category audit row.
/// </summary>
/// <remarks>
/// The frozen Pro <see cref="CsatCapture"/> carries the correlated <c>ConversationId</c> but no tenant
/// (the tenant is out of the producer wire shape — design D4), so the sink recovers the row's tenant
/// partition by resolving the conversation cross-tenant
/// (<see cref="IConversationStore.FindByIdAcrossTenantsAsync"/>). A capture whose conversation cannot be
/// resolved is dropped rather than persisted under an unknown tenant.
/// </remarks>
internal sealed class CsatVoiceCaptureSinkAdapter : ICsatVoiceCaptureSink
{
    private readonly IConversationStore _conversations;
    private readonly ISurveyResponseStore _responseStore;
    private readonly PlatformEventBus _eventBus;
    private readonly IAuditService _audit;

    public CsatVoiceCaptureSinkAdapter(
        IConversationStore conversations,
        ISurveyResponseStore responseStore,
        PlatformEventBus eventBus,
        IAuditService audit)
    {
        ArgumentNullException.ThrowIfNull(conversations);
        ArgumentNullException.ThrowIfNull(responseStore);
        ArgumentNullException.ThrowIfNull(eventBus);
        ArgumentNullException.ThrowIfNull(audit);
        _conversations = conversations;
        _responseStore = responseStore;
        _eventBus = eventBus;
        _audit = audit;
    }

    public async ValueTask SubmitAsync(CsatCapture capture, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capture);

        // Guard against malformed correlation ids the adapter could not resolve.
        if (!EntityId.IsValid(capture.SurveyId) || !EntityId.IsValid(capture.ConversationId))
            return;

        var conversationId = EntityId.From(capture.ConversationId);

        // Recover the tenant from the correlated conversation (the capture carries no tenant).
        var conversation = await _conversations
            .FindByIdAcrossTenantsAsync(conversationId, cancellationToken).ConfigureAwait(false);
        if (conversation is null)
            return; // no tracked conversation for the id — drop rather than persist under an unknown tenant

        var tenantId = conversation.TenantId;
        var responseId = EntityId.New();

        var response = new SurveyResponse
        {
            ResponseId = responseId,
            SurveyId = EntityId.From(capture.SurveyId),
            TenantId = tenantId,
            ConversationId = conversationId,
            ContactId = conversation.ContactId,
            CallId = conversationId, // voice capture: join the recorded rating to the call (design D4)
            Answers = [new SurveyAnswer(EntityId.From(SurveyQuestionIds.CsatRating), capture.Rating.ToString(System.Globalization.CultureInfo.InvariantCulture))],
            SubmittedAt = capture.CapturedAt,
            Channel = capture.Channel,
            QueueName = capture.QueueName,
            Rating = capture.Rating,
            Comment = capture.Comment, // null for voice — DTMF carries no free text
            CapturedAt = capture.CapturedAt,
        };

        await _responseStore.SaveAsync(response, cancellationToken).ConfigureAwait(false);

        _eventBus.Publish(new CsatResponseRecordedEvent(
            TenantId: tenantId.Value,
            ResponseId: responseId.Value,
            SurveyId: capture.SurveyId,
            ConversationId: capture.ConversationId,
            Channel: capture.Channel,
            QueueName: capture.QueueName,
            Rating: capture.Rating,
            Comment: capture.Comment,
            CapturedAt: capture.CapturedAt));

        await _audit.RecordAsync(
            tenantId,
            category: "csat",
            action: "csat.response.recorded",
            severity: "info",
            actorId: "system",
            actorType: "system",
            targetId: responseId.Value,
            targetType: "survey_response",
            metadata: new Dictionary<string, string>
            {
                ["channel"] = capture.Channel,
                ["queue"] = capture.QueueName,
                ["rating"] = capture.Rating.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["conversationId"] = capture.ConversationId,
            },
            ct: cancellationToken).ConfigureAwait(false);
    }
}
