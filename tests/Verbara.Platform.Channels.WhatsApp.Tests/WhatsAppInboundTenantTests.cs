using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Core;

namespace Verbara.Platform.Channels.WhatsApp.Tests;

/// <summary>
/// whatsapp-works-for-real D5 — WhatsApp inbound is per tenant and fail-closed: the tenant's own
/// AppSecret (no process-wide fallback), the whole Meta batch, and each change bound to the tenant's
/// phone_number_id.
/// </summary>
public sealed class WhatsAppInboundTenantTests
{
    private const string TenantSecret = "tenant-app-secret";
    private const string GlobalSecret = "process-global-secret"; // what the removed WhatsAppOptions.AppSecret fallback held
    private const string TenantPhoneNumberId = "111000111";

    private static readonly TenantId Tenant = new("tenant-wa");

    private static WhatsAppWebhookHandler CreateHandler(
        IReadOnlyDictionary<string, string>? credentials,
        IMeterFactory? meterFactory = null)
    {
        var store = Substitute.For<ITenantChannelConfigStore>();
        store.GetAsync(Tenant, ChannelType.WhatsApp, Arg.Any<CancellationToken>())
            .Returns(credentials is null
                ? null
                : new TenantChannelConfig { TenantId = Tenant, Channel = ChannelType.WhatsApp, Credentials = credentials });
        return new WhatsAppWebhookHandler(store, NullLogger<WhatsAppWebhookHandler>.Instance, meterFactory);
    }

    private static Dictionary<string, string> Sign(ReadOnlyMemory<byte> body, string secret)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body.Span);
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["x-hub-signature-256"] = "sha256=" + Convert.ToHexString(hash).ToLowerInvariant(),
        };
    }

    private static string Message(string id, string text) => $$"""
        { "from": "15550000002", "id": "{{id}}", "timestamp": "1700000000", "type": "text", "text": { "body": "{{text}}" } }
        """;

    private static ReadOnlyMemory<byte> Payload(string phoneNumberId, params string[] messages) =>
        Encoding.UTF8.GetBytes($$"""
            {
              "object": "whatsapp_business_account",
              "entry": [{
                "id": "WABA",
                "changes": [{
                  "field": "messages",
                  "value": {
                    "messaging_product": "whatsapp",
                    "metadata": { "display_phone_number": "15550000001", "phone_number_id": "{{phoneNumberId}}" },
                    "messages": [{{string.Join(",", messages)}}]
                  }
                }]
              }]
            }
            """);

    [Fact]
    public async Task HandleAsync_ShouldReturnIgnored_WhenTenantHasNoAppSecret()
    {
        // The tenant's configuration carries no AppSecret; the body is signed with the process-wide
        // option secret, which must never be used as a fallback.
        var handler = CreateHandler(new Dictionary<string, string>
        {
            [WhatsAppKeys.PhoneNumberId] = TenantPhoneNumberId,
        });
        var body = Payload(TenantPhoneNumberId, Message("wamid.g1", "hi"));

        var result = await handler.HandleAsync(body, Sign(body, GlobalSecret), Tenant, CancellationToken.None);

        result.Type.Should().Be(WebhookResultType.Ignored);
    }

    [Fact]
    public async Task HandleAsync_ShouldReturnTwoMessages_WhenMetaBatchesTwo()
    {
        var handler = CreateHandler(new Dictionary<string, string>
        {
            [WhatsAppKeys.AppSecret] = TenantSecret,
            [WhatsAppKeys.PhoneNumberId] = TenantPhoneNumberId,
        });
        var body = Payload(TenantPhoneNumberId, Message("wamid.b1", "first"), Message("wamid.b2", "second"));

        var result = await handler.HandleAsync(body, Sign(body, TenantSecret), Tenant, CancellationToken.None);

        result.Should().BeEquivalentTo(new
        {
            Type = WebhookResultType.NewMessage,
            Messages = new[] { new { ExternalMessageId = "wamid.b1" }, new { ExternalMessageId = "wamid.b2" } },
        });
    }

    [Fact]
    public async Task HandleAsync_ShouldReturnIgnored_WhenPhoneNumberIdIsNotTheTenants()
    {
        var handler = CreateHandler(new Dictionary<string, string>
        {
            [WhatsAppKeys.AppSecret] = TenantSecret,
            [WhatsAppKeys.PhoneNumberId] = TenantPhoneNumberId,
        });
        // Correctly signed with the tenant's secret (a shared Meta app), but for another tenant's number.
        var body = Payload("999000999", Message("wamid.other", "not yours"));

        var result = await handler.HandleAsync(body, Sign(body, TenantSecret), Tenant, CancellationToken.None);

        result.Type.Should().Be(WebhookResultType.Ignored);
    }

    [Fact]
    public async Task HandleAsync_ShouldReturnMessagesAndStatuses_WhenBatchMixesBoth()
    {
        var handler = CreateHandler(new Dictionary<string, string>
        {
            [WhatsAppKeys.AppSecret] = TenantSecret,
            [WhatsAppKeys.PhoneNumberId] = TenantPhoneNumberId,
        });
        ReadOnlyMemory<byte> body = Encoding.UTF8.GetBytes($$"""
            {
              "object": "whatsapp_business_account",
              "entry": [
                { "id": "WABA", "changes": [{ "field": "messages", "value": {
                    "metadata": { "phone_number_id": "{{TenantPhoneNumberId}}" },
                    "statuses": [
                      { "id": "wamid.s1", "status": "delivered", "timestamp": "1700000001", "recipient_id": "1" },
                      { "id": "wamid.s2", "status": "read", "timestamp": "1700000002", "recipient_id": "1" }
                    ],
                    "messages": [{{Message("wamid.m1", "one")}}] } }] },
                { "id": "WABA", "changes": [{ "field": "messages", "value": {
                    "metadata": { "phone_number_id": "999000999" },
                    "messages": [{{Message("wamid.foreign", "not ours")}}] } }] }
              ]
            }
            """);

        var result = await handler.HandleAsync(body, Sign(body, TenantSecret), Tenant, CancellationToken.None);

        result.Type.Should().Be(WebhookResultType.NewMessage);
        result.Messages.Select(m => m.ExternalMessageId).Should().Equal("wamid.m1");
        result.StatusUpdates.Select(u => u.ExternalMessageId).Should().Equal("wamid.s1", "wamid.s2");
    }

    [Fact]
    public async Task HandleAsync_ShouldReturnIgnored_WhenTenantHasNoPhoneNumberId()
    {
        var handler = CreateHandler(new Dictionary<string, string> { [WhatsAppKeys.AppSecret] = TenantSecret });
        var body = Payload(TenantPhoneNumberId, Message("wamid.np", "hi"));

        var result = await handler.HandleAsync(body, Sign(body, TenantSecret), Tenant, CancellationToken.None);

        result.Type.Should().Be(WebhookResultType.Ignored);
        result.Messages.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null, "bad_signature")]
    [InlineData("", "missing_app_secret")]
    public async Task HandleAsync_ShouldCountRejection_WhenDeliveryIsRefused(string? secretOverride, string reason)
    {
        var credentials = new Dictionary<string, string> { [WhatsAppKeys.PhoneNumberId] = TenantPhoneNumberId };
        credentials[WhatsAppKeys.AppSecret] = secretOverride ?? TenantSecret;
        using var meterFactory = new TestMeterFactory();
        var handler = CreateHandler(credentials, meterFactory);
        var measurements = new List<(long Value, string? Reason)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            // Only this handler's meter: other tests in the assembly run handlers concurrently.
            if (meterFactory.Meters.Contains(instrument.Meter) && instrument.Name == WhatsAppWebhookHandler.RejectedCounterName)
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            string? tagReason = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "reason")
                    tagReason = tag.Value as string;
            }
            measurements.Add((value, tagReason));
        });
        listener.Start();
        var body = Payload(TenantPhoneNumberId, Message("wamid.r", "hi"));

        // Signed with a secret that is never the tenant's.
        var result = await handler.HandleAsync(body, Sign(body, "forged-secret"), Tenant, CancellationToken.None);

        result.Type.Should().Be(WebhookResultType.Ignored);
        measurements.Should().Equal((1L, reason));
    }

    private static class WhatsAppKeys
    {
        public const string AppSecret = WhatsAppCredentialKeys.AppSecret;
        public const string PhoneNumberId = WhatsAppCredentialKeys.PhoneNumberId;
    }

    private sealed class TestMeterFactory : IMeterFactory
    {
        public List<Meter> Meters { get; } = [];

        public Meter Create(MeterOptions options)
        {
            var meter = new Meter(options);
            Meters.Add(meter);
            return meter;
        }

        public void Dispose()
        {
            foreach (var meter in Meters)
                meter.Dispose();
        }
    }
}
