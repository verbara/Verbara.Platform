using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// whatsapp-works-for-real D18 — the admission rule for a digital channel, as an executable gate: every
/// webhook handler the host REGISTERS (enumerated from the live registry, not a hand-kept list) must ignore
/// an unsigned body for a tenant whose channel is configured, and write nothing. Each registered channel
/// needs a provider-shaped probe; a probe must be accepted when correctly signed (positive control), so a
/// probe that no handler would ever accept cannot make the gate pass. Registering SMS, Telegram, Twitter,
/// RCS or Email before their signature checks fail closed turns this red.
/// </summary>
public sealed class RegisteredWebhookHandlerGateTests : IClassFixture<PlatformApiFactory>
{
    private readonly PlatformApiFactory _factory;

    public RegisteredWebhookHandlerGateTests(PlatformApiFactory factory) => _factory = factory;

    [Fact]
    public async Task RegisteredWebhookHandlers_ShouldIgnoreUnsignedBodies_ForEveryRegisteredChannel()
    {
        var registered = _factory.Services.GetRequiredService<IChannelRegistry>().WebhookChannels;

        var violations = await FindViolationsAsync(_factory, WebhookProbes.ByChannel);

        registered.Should().NotBeEmpty("the host registers WhatsApp; an empty set would make this gate vacuous");
        violations.Should().BeEmpty();
    }

    [Fact]
    public void RegisteredWebhookHandlers_ShouldBeExactlyWhatsApp_WhenHostComposesDigitalChannels()
    {
        // DQ1: WhatsApp is the only webhook channel this release serves (WebChat has its own endpoints).
        _factory.Services.GetRequiredService<IChannelRegistry>().WebhookChannels
            .Should().BeEquivalentTo([ChannelType.WhatsApp]);
        _factory.Services.GetRequiredService<IChannelRegistry>().AvailableChannels
            .Should().BeEquivalentTo([ChannelType.WhatsApp, ChannelType.WebChat]);
    }

    [Fact]
    public async Task RegisteredWebhookHandlers_ShouldNameTheChannel_WhenAFailOpenHandlerIsRegistered()
    {
        // The gate's own mutation test: a host that additionally registers a handler accepting unsigned
        // bodies (what SMS does today) must produce a violation naming that channel.
        await using var mutant = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddSingleton<IWebhookHandler>(new FailOpenWebhookHandler(ChannelType.Sms))));
        var probes = new Dictionary<ChannelType, WebhookProbe>(WebhookProbes.ByChannel)
        {
            [ChannelType.Sms] = FailOpenWebhookHandler.Probe,
        };

        var violations = await FindViolationsAsync(mutant, probes);

        violations.Should().NotBeEmpty()
            .And.OnlyContain(v => v.StartsWith("Sms:", StringComparison.Ordinal))
            .And.Contain(v => v.Contains("accepted an unsigned body", StringComparison.Ordinal))
            .And.Contain(v => v.Contains("unsigned POST", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RegisteredWebhookHandlers_ShouldReportMissingProbe_WhenARegisteredChannelHasNone()
    {
        await using var mutant = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddSingleton<IWebhookHandler>(new FailOpenWebhookHandler(ChannelType.Telegram))));

        var violations = await FindViolationsAsync(mutant, WebhookProbes.ByChannel);

        violations.Should().ContainSingle().Which.Should().StartWith("Telegram:").And.Contain("no probe");
    }

    [Fact]
    public async Task PostWebhook_ShouldWriteNothing_WhenChannelIsConfiguredButNotRegistered()
    {
        // digital-channel-registry: a tenant with an active SMS configuration still cannot reach SMS.
        var tenant = WebhookProbes.NewTenant();
        await WebhookProbes.ConfigureAsync(_factory.Services, tenant, ChannelType.Sms, FailOpenWebhookHandler.Probe.Credentials);
        using var client = _factory.CreateClient();
        using var content = new ByteArrayContent(FailOpenWebhookHandler.Probe.Body);

        var response = await client.PostAsync($"/api/v1/webhooks/{tenant.Value}/sms", content);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("registered");
        (await WebhookProbes.CountRowsAsync(_factory.Services, tenant)).Should().Be((0, 0, 0));
    }

    /// <summary>
    /// Runs the gate against <paramref name="host"/>: for every registered webhook channel, the handler must
    /// return <see cref="WebhookResultType.Ignored"/> with no events for the unsigned probe, the unsigned POST
    /// must write no row, and the signed probe must be accepted (or the probe proves nothing).
    /// </summary>
    private static async Task<IReadOnlyList<string>> FindViolationsAsync(
        WebApplicationFactory<Program> host,
        IReadOnlyDictionary<ChannelType, WebhookProbe> probes)
    {
        var registry = host.Services.GetRequiredService<IChannelRegistry>();
        using var client = host.CreateClient();
        var violations = new List<string>();

        foreach (var channel in registry.WebhookChannels)
        {
            if (!probes.TryGetValue(channel, out var probe))
            {
                violations.Add($"{channel}: registered webhook handler has no probe — add a provider-shaped body and signer to WebhookProbes.");
                continue;
            }

            var tenant = WebhookProbes.NewTenant();
            await WebhookProbes.ConfigureAsync(host.Services, tenant, channel, probe.Credentials);
            var route = $"/api/v1/webhooks/{tenant.Value}/{channel.ToString().ToLowerInvariant()}";

            var direct = await registry.GetHandler(channel).HandleAsync(
                probe.Body, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), tenant, CancellationToken.None);
            if (direct.Type != WebhookResultType.Ignored || direct.Messages.Count > 0 || direct.StatusUpdates.Count > 0)
                violations.Add($"{channel}: handler accepted an unsigned body ({direct.Type}, {direct.Messages.Count} message(s), {direct.StatusUpdates.Count} status(es)).");

            using (var unsigned = new ByteArrayContent(probe.Body))
            {
                var response = await client.PostAsync(route, unsigned);
                var rows = await WebhookProbes.CountRowsAsync(host.Services, tenant);
                if (response.StatusCode != HttpStatusCode.OK || rows != (0, 0, 0))
                    violations.Add($"{channel}: unsigned POST answered {(int)response.StatusCode} and wrote {rows} (contacts, conversations, messages); expected 200 and none.");
            }

            using (var signed = new ByteArrayContent(probe.Body))
            {
                foreach (var header in probe.Sign(probe.Body))
                    signed.Headers.TryAddWithoutValidation(header.Key, header.Value);
                var response = await client.PostAsync(route, signed);
                var rows = await WebhookProbes.CountRowsAsync(host.Services, tenant);
                if (response.StatusCode != HttpStatusCode.OK || rows.Conversations == 0)
                    violations.Add($"{channel}: positive control failed — the signed probe answered {(int)response.StatusCode} and wrote {rows}; the probe cannot prove the unsigned case.");
            }
        }

        return violations;
    }

    /// <summary>A handler that accepts any body without checking a signature — the mutant the gate must catch.</summary>
    private sealed class FailOpenWebhookHandler(ChannelType channel) : IWebhookHandler
    {
        public static readonly WebhookProbe Probe = new(
            new Dictionary<string, string> { ["AuthToken"] = "gate-sms-token" },
            "From=%2B15550000003&Body=hola&MessageSid=SMgate"u8.ToArray(),
            _ => new Dictionary<string, string>());

        public ChannelType Channel => channel;

        public Task<WebhookResult> HandleAsync(
            ReadOnlyMemory<byte> body, IReadOnlyDictionary<string, string> headers, TenantId tenantId, CancellationToken ct) =>
            Task.FromResult(WebhookResult.From(
                [new InboundMessage(
                    new ChannelAddress(channel, "+15550000003"),
                    new MessageEnvelope([new TextBlock("accepted without a signature")]),
                    $"failopen-{Guid.NewGuid():N}",
                    DateTimeOffset.UtcNow)],
                []));
    }
}

/// <summary>What the gate posts to one registered channel: the tenant configuration, a body, and its signer.</summary>
internal sealed record WebhookProbe(
    IReadOnlyDictionary<string, string> Credentials,
    byte[] Body,
    Func<byte[], IReadOnlyDictionary<string, string>> Sign);

/// <summary>Provider-shaped probes, one per channel the host may register, and the row counter the gate reads.</summary>
internal static class WebhookProbes
{
    private const string WhatsAppAppSecret = "gate-whatsapp-app-secret";
    private const string WhatsAppPhoneNumberId = "106540352242999";

    public static readonly IReadOnlyDictionary<ChannelType, WebhookProbe> ByChannel = new Dictionary<ChannelType, WebhookProbe>
    {
        [ChannelType.WhatsApp] = new(
            new Dictionary<string, string>
            {
                ["AccessToken"] = "EAAgate",
                ["PhoneNumberId"] = WhatsAppPhoneNumberId,
                ["AppSecret"] = WhatsAppAppSecret,
                ["WebhookVerifyToken"] = "gate-verify",
            },
            Encoding.UTF8.GetBytes($$"""
                {
                  "object": "whatsapp_business_account",
                  "entry": [{ "id": "WABA", "changes": [{ "field": "messages", "value": {
                    "messaging_product": "whatsapp",
                    "metadata": { "display_phone_number": "15550000001", "phone_number_id": "{{WhatsAppPhoneNumberId}}" },
                    "contacts": [{ "profile": { "name": "Gate" }, "wa_id": "15550000004" }],
                    "messages": [{ "from": "15550000004", "id": "wamid.gate", "timestamp": "1700000000", "type": "text", "text": { "body": "gate" } }]
                  } }] }]
                }
                """),
            body => new Dictionary<string, string>
            {
                ["X-Hub-Signature-256"] = "sha256=" + Convert.ToHexString(
                    HMACSHA256.HashData(Encoding.UTF8.GetBytes(WhatsAppAppSecret), body)).ToLowerInvariant(),
            }),
    };

    public static TenantId NewTenant() => new($"gate-{Guid.NewGuid():N}"[..20]);

    public static async Task ConfigureAsync(
        IServiceProvider services, TenantId tenant, ChannelType channel, IReadOnlyDictionary<string, string> credentials)
    {
        await services.GetRequiredService<ITenantChannelConfigStore>().SaveAsync(new TenantChannelConfig
        {
            TenantId = tenant,
            Channel = channel,
            Credentials = credentials,
        }, CancellationToken.None);

        await services.GetRequiredService<IQueueStore>().SaveAsync(new Queue
        {
            QueueId = EntityId.New(),
            TenantId = tenant,
            Name = "gate-queue",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);
    }

    public static async Task<(int Contacts, int Conversations, int Messages)> CountRowsAsync(IServiceProvider services, TenantId tenant)
    {
        var contacts = await services.GetRequiredService<IContactStore>()
            .SearchAsync(tenant, null, new PagedQuery(1, 100), CancellationToken.None);
        var conversations = await services.GetRequiredService<IConversationStore>()
            .ListAsync(tenant, new ConversationQuery { PageSize = 100 }, CancellationToken.None);
        var messages = 0;
        foreach (var conversation in conversations.Items)
        {
            messages += (await services.GetRequiredService<IMessageStore>()
                .GetConversationMessagesAsync(tenant, conversation.ConversationId, 100, 0, CancellationToken.None)).Count;
        }

        return (contacts.TotalCount, conversations.TotalCount, messages);
    }
}
