using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Surveys;

namespace Verbara.Platform.Surveys.Tests;

public class SurveyDeliveryTests
{
    private readonly IConversationService _conversationService = Substitute.For<IConversationService>();
    private readonly DefaultSurveyDeliveryService _delivery;

    private readonly TenantId _tenant = new("t1");
    private readonly EntityId _conversationId = EntityId.From("conv-001");
    private readonly EntityId _senderId = EntityId.From("bot-001");

    public SurveyDeliveryTests()
    {
        _delivery = new DefaultSurveyDeliveryService(_conversationService);

        _conversationService
            .SendMessageAsync(
                Arg.Any<EntityId>(), Arg.Any<TenantId>(), Arg.Any<MessageEnvelope>(),
                Arg.Any<EntityId>(), Arg.Any<ConversationOwnerKind>(), Arg.Any<CancellationToken>())
            .Returns(MakeMessage());
    }

    [Fact]
    public async Task DeliverAsync_ShouldSendInteractiveBlock_ForScaleQuestion()
    {
        var survey = MakeSurvey(SurveyQuestionType.Scale, "Rate 1-5");

        await _delivery.DeliverAsync(survey, _conversationId, _tenant, _senderId, CancellationToken.None);

        await _conversationService.Received(1).SendMessageAsync(
            _conversationId, _tenant,
            Arg.Is<MessageEnvelope>(e => e.Blocks[0] is InteractiveBlock),
            _senderId, ConversationOwnerKind.Bot, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeliverAsync_ShouldSendFiveReplies_ForScaleQuestion()
    {
        var survey = MakeSurvey(SurveyQuestionType.Scale, "Rate 1-5");
        MessageEnvelope? captured = null;
        _conversationService
            .SendMessageAsync(
                Arg.Any<EntityId>(), Arg.Any<TenantId>(),
                Arg.Do<MessageEnvelope>(e => captured = e),
                Arg.Any<EntityId>(), Arg.Any<ConversationOwnerKind>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(MakeMessage()));

        await _delivery.DeliverAsync(survey, _conversationId, _tenant, _senderId, CancellationToken.None);

        captured.Should().NotBeNull();
        var interactive = captured!.Blocks[0].Should().BeOfType<InteractiveBlock>().Subject;
        interactive.Replies.Should().HaveCount(5);
        interactive.Replies.Select(r => r.Id).Should().Equal("1", "2", "3", "4", "5");
    }

    [Fact]
    public async Task DeliverAsync_ShouldSendTextBlock_ForFreeTextQuestion()
    {
        var survey = MakeSurvey(SurveyQuestionType.FreeText, "Any comments?");

        await _delivery.DeliverAsync(survey, _conversationId, _tenant, _senderId, CancellationToken.None);

        await _conversationService.Received(1).SendMessageAsync(
            _conversationId, _tenant,
            Arg.Is<MessageEnvelope>(e => e.Blocks[0] is TextBlock),
            _senderId, ConversationOwnerKind.Bot, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeliverAsync_ShouldSendInteractiveBlock_ForChoiceQuestion()
    {
        var survey = MakeSurvey(SurveyQuestionType.Choice, "What is your issue?",
            ["Billing", "Technical", "Other"]);

        await _delivery.DeliverAsync(survey, _conversationId, _tenant, _senderId, CancellationToken.None);

        await _conversationService.Received(1).SendMessageAsync(
            _conversationId, _tenant,
            Arg.Is<MessageEnvelope>(e => e.Blocks[0] is InteractiveBlock),
            _senderId, ConversationOwnerKind.Bot, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeliverAsync_ShouldSendOneMessagePerQuestion()
    {
        var survey = new Survey
        {
            SurveyId = EntityId.New(),
            TenantId = _tenant,
            Name = "Multi-Q",
            Type = SurveyType.Custom,
            Questions =
            [
                new SurveyQuestion { QuestionId = EntityId.New(), Text = "Q1", Type = SurveyQuestionType.Scale },
                new SurveyQuestion { QuestionId = EntityId.New(), Text = "Q2", Type = SurveyQuestionType.FreeText },
                new SurveyQuestion { QuestionId = EntityId.New(), Text = "Q3", Type = SurveyQuestionType.Choice, Options = ["A", "B"] },
            ],
        };

        await _delivery.DeliverAsync(survey, _conversationId, _tenant, _senderId, CancellationToken.None);

        await _conversationService.Received(3).SendMessageAsync(
            Arg.Any<EntityId>(), Arg.Any<TenantId>(), Arg.Any<MessageEnvelope>(),
            Arg.Any<EntityId>(), Arg.Any<ConversationOwnerKind>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeliverAsync_ShouldSendAsBotOwnerKind()
    {
        var survey = MakeSurvey(SurveyQuestionType.FreeText, "How was it?");

        await _delivery.DeliverAsync(survey, _conversationId, _tenant, _senderId, CancellationToken.None);

        await _conversationService.Received(1).SendMessageAsync(
            Arg.Any<EntityId>(), Arg.Any<TenantId>(), Arg.Any<MessageEnvelope>(),
            _senderId, ConversationOwnerKind.Bot, Arg.Any<CancellationToken>());
    }

    // -------------------------------------------------------------------------

    private Survey MakeSurvey(SurveyQuestionType questionType, string text,
        IReadOnlyList<string>? options = null) =>
        new()
        {
            SurveyId = EntityId.New(),
            TenantId = _tenant,
            Name = "Test Survey",
            Type = SurveyType.Csat,
            Questions =
            [
                new SurveyQuestion
                {
                    QuestionId = EntityId.New(),
                    Text = text,
                    Type = questionType,
                    Options = options,
                },
            ],
        };

    private Message MakeMessage() =>
        new()
        {
            MessageId = EntityId.New(),
            ConversationId = _conversationId,
            TenantId = _tenant,
            Direction = MessageDirection.Outbound,
            Channel = ChannelType.WebChat,
            Content = new MessageEnvelope([new TextBlock("placeholder")]),
            DeliveryStatus = MessageDeliveryStatus.Sent,
            CreatedAt = DateTimeOffset.UtcNow,
        };
}
