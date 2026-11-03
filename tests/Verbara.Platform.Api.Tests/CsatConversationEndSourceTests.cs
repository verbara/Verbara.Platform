using Verbara.Platform.Api.Services;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Surveys;
using Verbara.Sdk.Pro.CsatRunner.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// conversations-close-after-inactivity (design D6; spec <c>conversation-inactivity-close</c>, "A system close MUST NOT
/// solicit CSAT"): a conversation closed by the system ("sin aviso") pushes no CSAT signal, whatever its queue's CSAT
/// configuration; a close with no system reason still solicits as before.
/// </summary>
public sealed class CsatConversationEndSourceTests
{
    private static readonly TenantId Tenant = new("ten-csat");
    private static readonly EntityId QueueId = EntityId.From("queue-csat");
    private static readonly EntityId ContactId = EntityId.From("contact-csat");
    private static readonly EntityId ConversationId = EntityId.From("conv-csat");

    [Theory]
    [InlineData(ConversationCloseReasons.Inactivity)]
    [InlineData(ConversationCloseReasons.CustomerReturned)]
    public async Task HandleClosedAsync_ShouldPushNoSignal_WhenTheCloseIsASystemClose(string reason)
    {
        var pushed = await HandleAsync(closedReason: reason);

        Assert.Empty(pushed);
    }

    [Fact]
    public async Task HandleClosedAsync_ShouldPushOneSignal_WhenTheCloseCarriesNoSystemReason()
    {
        var pushed = await HandleAsync(closedReason: null);

        var signal = Assert.Single(pushed);
        Assert.Equal(ConversationId.Value, signal.ConversationId);
        Assert.True(signal.CsatEnabled);
    }

    /// <summary>A queue-owned WebChat conversation, CSAT enabled on its queue and an active CSAT survey, closed.</summary>
    private static async Task<List<CsatConversationEndedSignal>> HandleAsync(string? closedReason)
    {
        var conversation = new Conversation
        {
            ConversationId = ConversationId,
            TenantId = Tenant,
            ContactId = ContactId,
            Channel = ChannelType.WebChat,
            State = ConversationState.Closed,
            Owner = ConversationOwner.ForQueue(QueueId),
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-4),
        };
        if (closedReason is not null)
            conversation.SetMetadata(ConversationCloseReasons.MetadataKey, closedReason);

        var conversationStore = Substitute.For<IConversationStore>();
        conversationStore.GetByIdAsync(Tenant, ConversationId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Conversation?>(conversation));

        var queueStore = Substitute.For<IQueueStore>();
        queueStore.GetByIdAsync(Tenant, QueueId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Queue?>(new Queue
            {
                QueueId = QueueId,
                TenantId = Tenant,
                Name = "support",
                Csat = new CsatConfig(Enabled: true, PreferredChannel: "webchat", PromptTemplateId: null, SamplingRatePercent: 100),
                CreatedAt = DateTimeOffset.UtcNow,
            }));

        var surveyStore = Substitute.For<ISurveyStore>();
        surveyStore.GetActiveAsync(Tenant, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Survey>>([new Survey
            {
                SurveyId = EntityId.From("srv-csat"),
                TenantId = Tenant,
                Name = "CSAT",
                Type = SurveyType.Csat,
                Questions = [],
            }]));

        var contactStore = Substitute.For<IContactStore>();
        contactStore.GetByIdAsync(Tenant, ContactId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Contact?>(new Contact { ContactId = ContactId, TenantId = Tenant, CreatedAt = DateTimeOffset.UtcNow }));

        using var bus = new PlatformEventBus();
        var source = new CsatConversationEndSource(
            bus, conversationStore, queueStore, surveyStore, contactStore, NullLogger<CsatConversationEndSource>.Instance);

        var pushed = new List<CsatConversationEndedSignal>();
        using var sub = source.Ended.Subscribe(pushed.Add);

        await source.HandleClosedAsync(
            new ConversationStateChangedEvent(Tenant.Value, ConversationId.Value, nameof(ConversationState.Active), nameof(ConversationState.Closed)),
            CancellationToken.None);
        return pushed;
    }
}
