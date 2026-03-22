using Asterisk.Platform.Conversations;
using Asterisk.Platform.Conversations.Services;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Surveys;

/// <summary>
/// Sends survey questions to a contact by building <see cref="MessageEnvelope"/> payloads
/// and delegating to <see cref="IConversationService.SendMessageAsync"/>.
/// </summary>
public sealed class DefaultSurveyDeliveryService : ISurveyDeliveryService
{
    private readonly IConversationService _conversationService;

    public DefaultSurveyDeliveryService(IConversationService conversationService)
    {
        _conversationService = conversationService;
    }

    /// <inheritdoc />
    public async Task DeliverAsync(Survey survey, EntityId conversationId, TenantId tenantId, EntityId senderId, CancellationToken ct)
    {
        foreach (var question in survey.Questions)
        {
            var block = BuildBlock(question);
            var envelope = new MessageEnvelope([block]);
            await _conversationService.SendMessageAsync(
                conversationId,
                tenantId,
                envelope,
                senderId,
                ConversationOwnerKind.Bot,
                ct).ConfigureAwait(false);
        }
    }

    // -------------------------------------------------------------------------

    private static MessageBlock BuildBlock(SurveyQuestion question) =>
        question.Type switch
        {
            SurveyQuestionType.Scale => BuildScaleBlock(question),
            SurveyQuestionType.Choice => BuildChoiceBlock(question),
            _ => new TextBlock(question.Text),
        };

    private static InteractiveBlock BuildScaleBlock(SurveyQuestion question)
    {
        var replies = new List<QuickReply>
        {
            new("1", "1"),
            new("2", "2"),
            new("3", "3"),
            new("4", "4"),
            new("5", "5"),
        };
        return new InteractiveBlock(question.Text, replies);
    }

    private static InteractiveBlock BuildChoiceBlock(SurveyQuestion question)
    {
        var options = question.Options ?? [];
        var replies = options
            .Select((opt, idx) => new QuickReply(idx.ToString(System.Globalization.CultureInfo.InvariantCulture), opt))
            .ToList();
        return new InteractiveBlock(question.Text, replies);
    }
}
