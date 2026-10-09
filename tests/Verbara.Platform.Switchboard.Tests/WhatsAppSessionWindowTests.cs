using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;
using Verbara.Platform.Switchboard;

namespace Verbara.Platform.Switchboard.Tests;

/// <summary>whatsapp-works-for-real task 3.4 (whatsapp-outbound, design D7): the 24-hour window from the store.</summary>
public sealed class WhatsAppSessionWindowTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly TenantId Tenant = new("tenant-1");
    private static readonly EntityId ConversationId = EntityId.From("conv-wa-1");

    [Fact]
    public void Decide_ShouldSendFreeForm_WhenLastInboundIs23h59mOld()
    {
        var decision = WhatsAppSessionWindow.Decide(Now - TimeSpan.FromHours(24) + TimeSpan.FromMinutes(1), Now, templateId: null);

        decision.Mode.Should().Be(WhatsAppSendMode.FreeForm);
        decision.IsRefused.Should().BeFalse();
        decision.RefusalCode.Should().BeNull();
    }

    [Fact]
    public void Decide_ShouldRequireTemplate_WhenLastInboundIs24h01mOld()
    {
        var decision = WhatsAppSessionWindow.Decide(Now - TimeSpan.FromHours(24) - TimeSpan.FromMinutes(1), Now, templateId: null);

        decision.Mode.Should().Be(WhatsAppSendMode.TemplateRequired);
        decision.IsRefused.Should().BeTrue();
        decision.RefusalCode.Should().Be("whatsapp-template-required");
    }

    [Fact]
    public void Decide_ShouldRequireTemplate_WhenExactly24hHavePassed()
    {
        WhatsAppSessionWindow.Decide(Now - TimeSpan.FromHours(24), Now, templateId: null)
            .Mode.Should().Be(WhatsAppSendMode.TemplateRequired);
    }

    [Fact]
    public void Decide_ShouldRequireTemplate_WhenConversationHasNoInbound()
    {
        WhatsAppSessionWindow.Decide(null, Now, templateId: null).Mode.Should().Be(WhatsAppSendMode.TemplateRequired);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(48)]
    public void Decide_ShouldSendTemplate_WhenCallerNamesOne(int hoursSinceInbound)
    {
        WhatsAppSessionWindow.Decide(Now - TimeSpan.FromHours(hoursSinceInbound), Now, templateId: "order_update")
            .Mode.Should().Be(WhatsAppSendMode.Template);
    }

    [Fact]
    public async Task DecideAsync_ShouldReadLastInboundFromStore_WhenWindowOpen()
    {
        var store = Substitute.For<IMessageStore>();
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(Now);
        store.FindLastInboundAsync(Tenant, ConversationId, Arg.Any<CancellationToken>()).Returns(new Message
        {
            MessageId = EntityId.New(),
            ConversationId = ConversationId,
            TenantId = Tenant,
            Direction = MessageDirection.Inbound,
            Channel = ChannelType.WhatsApp,
            Content = new MessageEnvelope([]),
            DeliveryStatus = MessageDeliveryStatus.Delivered,
            CreatedAt = Now.AddHours(-1),
        });

        var decision = await new WhatsAppSessionWindow(store, clock).DecideAsync(Tenant, ConversationId, null, CancellationToken.None);

        decision.Mode.Should().Be(WhatsAppSendMode.FreeForm);
        decision.LastInboundAt.Should().Be(Now.AddHours(-1));
    }

    [Fact]
    public async Task DecideAsync_ShouldRequireTemplate_WhenStoreHasNoInbound()
    {
        var store = Substitute.For<IMessageStore>();
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(Now);
        store.FindLastInboundAsync(Tenant, ConversationId, Arg.Any<CancellationToken>()).Returns((Message?)null);

        var decision = await new WhatsAppSessionWindow(store, clock).DecideAsync(Tenant, ConversationId, null, CancellationToken.None);

        decision.RefusalCode.Should().Be(WhatsAppWindowDecision.TemplateRequiredCode);
    }
}
