using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Verbara.Platform.Api.Tests.Conversations;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;

namespace Verbara.Platform.Api.Tests.Sse;

/// <summary>
/// The conversation-ownership host (real JWTs, role-template permissions, in-memory stores) for the live-stream
/// content suites (contact-history-opens-by-assignment task 1.9, design D17): the conversation store behind a
/// <see cref="ProbeConversationStore"/>, WebChat active for the customer tenant, and a signed WhatsApp webhook, so each
/// publisher is driven through its real path and each subscriber reads its own <c>/events/stream</c>.
/// </summary>
public class ContentDeliveryApiFactory : ConversationOwnershipApiFactory
{
    public const string WhatsAppAppSecret = "content-delivery-wa-secret";
    private const string WhatsAppPhoneNumberId = "106540352242999";

    private readonly Lock _whatsAppLock = new();
    private bool _whatsAppConfigured;

    /// <summary>Whether every read and write of the conversation store copies the row (the Postgres semantics).</summary>
    protected virtual bool DetachedConversations => false;

    /// <summary>The store wrapper the host resolves as <see cref="IConversationStore"/>.</summary>
    internal ProbeConversationStore ConversationProbe =>
        (ProbeConversationStore)Services.GetRequiredService<IConversationStore>();

    /// <summary>The host's event bus.</summary>
    public PlatformEventBus Bus => Services.GetRequiredService<PlatformEventBus>();

    /// <inheritdoc />
    protected override void ConfigureTestServices(IServiceCollection services)
    {
        base.ConfigureTestServices(services);

        var real = services.Last(descriptor => descriptor.ServiceType == typeof(IConversationStore));
        services.Remove(real);
        services.AddSingleton<IConversationStore>(provider =>
            new ProbeConversationStore((IConversationStore)real.ImplementationFactory!(provider), DetachedConversations));
    }

    /// <summary>Activates WebChat for the customer tenant, which <c>POST /webchat/sessions</c> requires.</summary>
    public void ActivateWebChat() => AuthenticatedPlatformApiFactory.SeedWebChatChannel(Services, Tenant.Value);

    /// <summary>Saves the WhatsApp credentials (and a queue) of the customer tenant, once per host.</summary>
    public void EnsureWhatsApp()
    {
        lock (_whatsAppLock)
        {
            if (_whatsAppConfigured)
                return;
            WebhookProbes.ConfigureAsync(Services, Tenant, ChannelType.WhatsApp, new Dictionary<string, string>
            {
                ["AccessToken"] = "EAAcontent",
                ["PhoneNumberId"] = WhatsAppPhoneNumberId,
                ["AppSecret"] = WhatsAppAppSecret,
                ["WebhookVerifyToken"] = "content-verify",
            }).GetAwaiter().GetResult();
            _whatsAppConfigured = true;
        }
    }

    /// <summary>Posts one signed WhatsApp text message from <paramref name="from"/> to the customer tenant's webhook.</summary>
    public async Task<HttpResponseMessage> PostWhatsAppAsync(string from, string text)
    {
        EnsureWhatsApp();
        var body = Encoding.UTF8.GetBytes($$"""
            {
              "object": "whatsapp_business_account",
              "entry": [{
                "id": "WABA",
                "changes": [{
                  "field": "messages",
                  "value": {
                    "messaging_product": "whatsapp",
                    "metadata": { "display_phone_number": "15550000001", "phone_number_id": "{{WhatsAppPhoneNumberId}}" },
                    "messages": [{ "from": "{{from}}", "id": "wamid.{{Guid.NewGuid():N}}", "timestamp": "1700000000", "type": "text", "text": { "body": "{{text}}" } }],
                    "statuses": []
                  }
                }]
              }]
            }
            """);
        using var client = CreateClient();
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(WhatsAppAppSecret), body);
        content.Headers.Add("X-Hub-Signature-256", "sha256=" + Convert.ToHexString(hash).ToLowerInvariant());
        return await client.PostAsync($"/api/v1/webhooks/{Tenant.Value}/whatsapp", content);
    }

    /// <summary>Rewrites who holds <paramref name="conversationId"/>: an owner, or an open offer from a queue.</summary>
    public async Task SetHolderAsync(EntityId conversationId, Agent? owner = null, Agent? offeredTo = null)
    {
        var store = Services.GetRequiredService<IConversationStore>();
        var conversation = (await store.GetByIdAsync(Tenant, conversationId, CancellationToken.None))!;
        conversation.RemoveMetadata("_offeredTo");
        conversation.RemoveMetadata("_offeredAt");
        if (owner is not null)
        {
            conversation.Owner = ConversationOwner.ForAgent(owner.AgentId);
            conversation.State = ConversationState.Active;
        }
        else if (offeredTo is not null)
        {
            conversation.Owner = ConversationOwner.ForQueue(SeedQueue().QueueId);
            conversation.State = ConversationState.Offered;
            conversation.SetMetadata("_offeredTo", offeredTo.AgentId.Value);
            conversation.SetMetadata("_offeredAt",
                DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        }

        await store.SaveAsync(conversation, CancellationToken.None);
    }

    /// <summary>Opens <paramref name="user"/>'s live stream and waits until it is subscribed to the bus.</summary>
    internal async Task<LiveStreamProbe> OpenStreamAsync(string name, Identity.User user)
    {
        var probe = await LiveStreamProbe.OpenAsync(name, ClientFor(user));
        await LiveStreamProbe.SyncAsync(Bus, Tenant.Value, probe);
        return probe;
    }
}

/// <summary><see cref="ContentDeliveryApiFactory"/> over a conversation store that copies every row it reads and writes.</summary>
public sealed class DetachedContentDeliveryApiFactory : ContentDeliveryApiFactory
{
    protected override bool DetachedConversations => true;
}

/// <summary>Records the <see cref="ConversationMessageEvent"/>s the host publishes, as the publisher wrote them.</summary>
internal sealed class MessageEventCapture : IDisposable
{
    private readonly List<ConversationMessageEvent> _events = [];
    private readonly IDisposable _subscription;

    public MessageEventCapture(PlatformEventBus bus) =>
        _subscription = bus.Events.Subscribe(new Observer(this));

    public IReadOnlyList<ConversationMessageEvent> Events
    {
        get
        {
            lock (_events)
                return [.. _events];
        }
    }

    /// <summary>Waits until a published message matches <paramref name="match"/>, and returns it.</summary>
    public async Task<ConversationMessageEvent> WaitForAsync(Func<ConversationMessageEvent, bool> match, string what)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (Events.FirstOrDefault(match) is { } found)
                return found;
            await Task.Delay(20); // fence-allow: LOOP-DRIVER — poll pacing until the publisher under test has published
        }

        throw new TimeoutException($"no conversation.message was published for {what}");
    }

    public void Dispose() => _subscription.Dispose();

    private sealed class Observer(MessageEventCapture owner) : IObserver<PlatformEvent>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(PlatformEvent value)
        {
            if (value is ConversationMessageEvent message)
            {
                lock (owner._events)
                    owner._events.Add(message);
            }
        }
    }
}
