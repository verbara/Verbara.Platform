using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Channels.WhatsApp;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;

namespace Verbara.Platform.Api.Tests.Conversations;

/// <summary>
/// conversations-close-after-inactivity task 2.4 (design D9; spec <c>conversation-customer-return</c>, "A reply crossing
/// the inactivity close is refused"), through the real host: the inactivity close commits after the send's state check
/// (at its WhatsApp window read) and before its claim. The reply is refused with the send endpoint's existing 400, no
/// message is stored and nothing reaches the provider.
/// </summary>
public sealed class ConversationSendClaimEndpointTests : IClassFixture<ConversationSendClaimEndpointTests.SendClaimApiFactory>
{
    private const string PhoneNumberId = "106540352242956";
    private readonly SendClaimApiFactory _factory;

    public ConversationSendClaimEndpointTests(SendClaimApiFactory factory) => _factory = factory;

    [Fact]
    public async Task SendMessage_ShouldReturn400AndStoreNothing_WhenTheInactivityCloseCommitsAfterTheStateCheck()
    {
        var (user, conversation) = await SeedAsync();
        var store = _factory.Services.GetRequiredService<IConversationStore>();
        var hooked = _factory.Messages;
        var closes = 0;
        // The WhatsApp window read: after the send's state check, before it stores or sends anything.
        hooked.BeforeFindLastInbound = async id =>
        {
            if (id != conversation.ConversationId || Interlocked.Increment(ref closes) != 1)
                return;
            var row = (await store.GetByIdAsync(ConversationOwnershipApiFactory.Tenant, id, CancellationToken.None))!;
            var closed = await store.TryCloseInactiveAsync(ConversationOwnershipApiFactory.Tenant, new InactivityCloseRequest(
                id, row.State, row.Owner,
                Cutoff: DateTimeOffset.UtcNow.AddMinutes(1), BacklogCutoff: DateTimeOffset.UtcNow.AddYears(-1),
                Now: DateTimeOffset.UtcNow), CancellationToken.None);
            closed.Should().NotBeNull("the sweep's close commits between the send's state check and its claim");
        };

        try
        {
            using var client = _factory.ClientFor(user);
            var response = await client.PostAsJsonAsync(
                $"/api/v1/conversations/{conversation.ConversationId.Value}/messages", new { text = "still there?" });

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        }
        finally
        {
            hooked.BeforeFindLastInbound = null;
        }

        var messages = await _factory.Services.GetRequiredService<IMessageStore>().GetConversationMessagesAsync(
            ConversationOwnershipApiFactory.Tenant, conversation.ConversationId, 100, 0, CancellationToken.None);
        messages.Where(m => m.Direction == MessageDirection.Outbound).Should().BeEmpty("a refused reply stores no message");
        _factory.Graph.RequestsFor(conversation.ConversationId).Should().BeEmpty("nothing reaches the provider");
        closes.Should().Be(1, "the window read ran, so the send passed its state check before the close");
        (await store.GetByIdAsync(ConversationOwnershipApiFactory.Tenant, conversation.ConversationId, CancellationToken.None))!
            .State.Should().Be(ConversationState.Closed);
    }

    private async Task<(Verbara.Platform.Identity.User User, Conversation Conversation)> SeedAsync()
    {
        await _factory.EnsureWhatsAppConfiguredAsync();
        var (user, agent) = _factory.NewAgent("send-claim");
        var services = _factory.Services;
        var contact = new Contact
        {
            ContactId = EntityId.From($"contact-{Guid.NewGuid():N}"),
            TenantId = ConversationOwnershipApiFactory.Tenant,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var address = $"1556{Random.Shared.Next(1_000_000, 9_999_999)}";
        contact.AddAddress(new ChannelAddress(ChannelType.WhatsApp, address));
        await services.GetRequiredService<IContactStore>().SaveAsync(contact, CancellationToken.None);

        var conversation = new Conversation
        {
            ConversationId = EntityId.From($"conv-{Guid.NewGuid():N}"),
            TenantId = ConversationOwnershipApiFactory.Tenant,
            ContactId = contact.ContactId,
            Channel = ChannelType.WhatsApp,
            State = ConversationState.Active,
            Owner = ConversationOwner.ForAgent(agent.AgentId),
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-1),
        };
        await services.GetRequiredService<IConversationStore>().SaveAsync(conversation, CancellationToken.None);
        _factory.Graph.Map(address, conversation.ConversationId);

        // An inbound message 5 minutes ago keeps the WhatsApp window open: the only refusal left is the close.
        var at = DateTimeOffset.UtcNow.AddMinutes(-5);
        await services.GetRequiredService<IMessageStore>().SaveAsync(new Message
        {
            MessageId = EntityId.New(),
            ConversationId = conversation.ConversationId,
            TenantId = ConversationOwnershipApiFactory.Tenant,
            Direction = MessageDirection.Inbound,
            Channel = ChannelType.WhatsApp,
            SenderId = address,
            Content = new MessageEnvelope([new TextBlock("hola")]),
            DeliveryStatus = MessageDeliveryStatus.Delivered,
            ExternalMessageId = $"wamid.in-{Guid.NewGuid():N}",
            CreatedAt = at,
            DeliveredAt = at,
        }, CancellationToken.None);

        return (user, conversation);
    }

    /// <summary>The ownership host with a Graph API stub and the message store behind <see cref="HookedMessageStore"/>.</summary>
    public sealed class SendClaimApiFactory : ConversationOwnershipApiFactory
    {
        private int _configured;

        public WhatsAppOutboundEndpointTests.GraphStub Graph { get; } = new();

        internal HookedMessageStore Messages => Services.GetRequiredService<HookedMessageStore>();

        protected override void ConfigureTestServices(IServiceCollection services)
        {
            base.ConfigureTestServices(services);
            services.AddHttpClient(WhatsAppConnector.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Graph);

            var registered = services.Last(d => d.ServiceType == typeof(IMessageStore));
            services.Remove(registered);
            services.AddSingleton(sp => new HookedMessageStore(registered.ImplementationFactory is { } factory
                ? (IMessageStore)factory(sp)
                : registered.ImplementationInstance is IMessageStore instance
                    ? instance
                    : (IMessageStore)ActivatorUtilities.CreateInstance(sp, registered.ImplementationType!)));
            services.AddSingleton<IMessageStore>(sp => sp.GetRequiredService<HookedMessageStore>());
        }

        public async Task EnsureWhatsAppConfiguredAsync()
        {
            if (Interlocked.Exchange(ref _configured, 1) == 1)
                return;

            await Services.GetRequiredService<ITenantChannelConfigStore>().SaveAsync(new TenantChannelConfig
            {
                TenantId = Tenant,
                Channel = ChannelType.WhatsApp,
                Credentials = new Dictionary<string, string>
                {
                    ["AccessToken"] = "EAAsendclaim",
                    ["PhoneNumberId"] = PhoneNumberId,
                    ["AppSecret"] = "send-claim-app-secret",
                    ["WebhookVerifyToken"] = "send-claim-verify",
                },
            }, CancellationToken.None);
        }
    }
}
