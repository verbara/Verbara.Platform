using Asterisk.Platform.Conversations.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Asterisk.Platform.Automation.Tests;

public class ConditionEvaluatorTests
{
    private readonly IContactStore _contactStore = Substitute.For<IContactStore>();
    private readonly DefaultConditionEvaluator _sut;

    private static readonly TenantId Tenant = new("t1");
    private static readonly EntityId ConvId = EntityId.From("conv-001");

    public ConditionEvaluatorTests()
    {
        _sut = new DefaultConditionEvaluator(_contactStore, NullLogger<DefaultConditionEvaluator>.Instance);
    }

    private static AutomationEvent BuildEvent(
        ChannelType? channel = null,
        ConversationState? state = null,
        Message? message = null,
        Dictionary<string, string>? metadata = null,
        EntityId? contactId = null)
    {
        return new AutomationEvent
        {
            Trigger = AutomationTrigger.MessageReceived,
            TenantId = Tenant,
            ConversationId = ConvId,
            Channel = channel,
            State = state,
            Message = message,
            Metadata = metadata,
            ContactId = contactId,
            OccurredAt = DateTimeOffset.UtcNow,
        };
    }

    private static AutomationCondition Cond(string field, ConditionOperator op, string? value = null) =>
        new() { Field = field, Operator = op, Value = value };

    // --- Equals / NotEquals ---

    [Fact]
    public async Task EvaluateAsync_ShouldReturnTrue_WhenEqualsMatchesChannel()
    {
        var ev = BuildEvent(channel: ChannelType.WhatsApp);
        var cond = Cond("channel", ConditionOperator.Equals, "WhatsApp");

        var result = await _sut.EvaluateAsync(cond, ev, default);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task EvaluateAsync_ShouldReturnFalse_WhenEqualsDoesNotMatchChannel()
    {
        var ev = BuildEvent(channel: ChannelType.Email);
        var cond = Cond("channel", ConditionOperator.Equals, "WhatsApp");

        var result = await _sut.EvaluateAsync(cond, ev, default);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task EvaluateAsync_ShouldReturnTrue_WhenNotEqualsDoesNotMatchChannel()
    {
        var ev = BuildEvent(channel: ChannelType.Sms);
        var cond = Cond("channel", ConditionOperator.NotEquals, "WhatsApp");

        var result = await _sut.EvaluateAsync(cond, ev, default);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task EvaluateAsync_ShouldReturnFalse_WhenNotEqualsMatchesChannel()
    {
        var ev = BuildEvent(channel: ChannelType.WhatsApp);
        var cond = Cond("channel", ConditionOperator.NotEquals, "WhatsApp");

        var result = await _sut.EvaluateAsync(cond, ev, default);

        result.Should().BeFalse();
    }

    // --- Contains / StartsWith ---

    [Fact]
    public async Task EvaluateAsync_ShouldReturnTrue_WhenContainsMatchesMessageContent()
    {
        var message = MakeMessage("Hello, I need help with my order");
        var ev = BuildEvent(message: message);
        var cond = Cond("message.content", ConditionOperator.Contains, "help");

        var result = await _sut.EvaluateAsync(cond, ev, default);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task EvaluateAsync_ShouldReturnTrue_WhenStartsWithMatchesMessageContent()
    {
        var message = MakeMessage("Hello, how are you?");
        var ev = BuildEvent(message: message);
        var cond = Cond("message.content", ConditionOperator.StartsWith, "Hello");

        var result = await _sut.EvaluateAsync(cond, ev, default);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task EvaluateAsync_ShouldReturnFalse_WhenStartsWithDoesNotMatch()
    {
        var message = MakeMessage("Goodbye!");
        var ev = BuildEvent(message: message);
        var cond = Cond("message.content", ConditionOperator.StartsWith, "Hello");

        var result = await _sut.EvaluateAsync(cond, ev, default);

        result.Should().BeFalse();
    }

    // --- GreaterThan / LessThan ---

    [Fact]
    public async Task EvaluateAsync_ShouldReturnTrue_WhenGreaterThanNumericComparison()
    {
        var ev = BuildEvent(metadata: new Dictionary<string, string> { ["priority"] = "5" });
        var cond = Cond("metadata.priority", ConditionOperator.GreaterThan, "3");

        var result = await _sut.EvaluateAsync(cond, ev, default);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task EvaluateAsync_ShouldReturnTrue_WhenLessThanNumericComparison()
    {
        var ev = BuildEvent(metadata: new Dictionary<string, string> { ["score"] = "2" });
        var cond = Cond("metadata.score", ConditionOperator.LessThan, "10");

        var result = await _sut.EvaluateAsync(cond, ev, default);

        result.Should().BeTrue();
    }

    // --- IsEmpty / IsNotEmpty ---

    [Fact]
    public async Task EvaluateAsync_ShouldReturnTrue_WhenIsEmptyAndFieldMissing()
    {
        var ev = BuildEvent(metadata: new Dictionary<string, string>());
        var cond = Cond("metadata.tag", ConditionOperator.IsEmpty);

        var result = await _sut.EvaluateAsync(cond, ev, default);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task EvaluateAsync_ShouldReturnTrue_WhenIsNotEmptyAndFieldHasValue()
    {
        var ev = BuildEvent(metadata: new Dictionary<string, string> { ["tag"] = "vip" });
        var cond = Cond("metadata.tag", ConditionOperator.IsNotEmpty);

        var result = await _sut.EvaluateAsync(cond, ev, default);

        result.Should().BeTrue();
    }

    // --- Field resolution ---

    [Fact]
    public async Task EvaluateAsync_ShouldResolveConversationState()
    {
        var ev = BuildEvent(state: ConversationState.Active);
        var cond = Cond("conversation.state", ConditionOperator.Equals, "Active");

        var result = await _sut.EvaluateAsync(cond, ev, default);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task EvaluateAsync_ShouldResolveMetadataKey()
    {
        var ev = BuildEvent(metadata: new Dictionary<string, string> { ["region"] = "EU" });
        var cond = Cond("metadata.region", ConditionOperator.Equals, "EU");

        var result = await _sut.EvaluateAsync(cond, ev, default);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task EvaluateAsync_ShouldResolveContactSegmentViaStore()
    {
        var contactId = EntityId.From("c-001");
        var contact = new Contact
        {
            ContactId = contactId,
            TenantId = Tenant,
            Segment = "premium",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _contactStore.GetByIdAsync(Tenant, contactId, default).Returns(contact);

        var ev = BuildEvent(contactId: contactId);
        var cond = Cond("contact.segment", ConditionOperator.Equals, "premium");

        var result = await _sut.EvaluateAsync(cond, ev, default);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task EvaluateAsync_ShouldReturnFalse_WhenFieldIsUnknown()
    {
        var ev = BuildEvent();
        var cond = Cond("unknown.field", ConditionOperator.Equals, "value");

        var result = await _sut.EvaluateAsync(cond, ev, default);

        result.Should().BeFalse();
    }

    private static Message MakeMessage(string text)
    {
        return new Message
        {
            MessageId = EntityId.New(),
            ConversationId = ConvId,
            TenantId = Tenant,
            Direction = MessageDirection.Inbound,
            Channel = ChannelType.WebChat,
            Content = new MessageEnvelope([new TextBlock(text)]),
            DeliveryStatus = MessageDeliveryStatus.Delivered,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }
}
