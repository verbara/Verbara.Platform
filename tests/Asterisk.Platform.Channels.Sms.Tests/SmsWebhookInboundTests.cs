using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Web;
using Asterisk.Platform.Channels.Core;
using Asterisk.Platform.Channels.Sms.Providers;
using Asterisk.Platform.Core;
using NSubstitute;

namespace Asterisk.Platform.Channels.Sms.Tests;

public class SmsWebhookInboundTests
{
    private static readonly TenantId Tenant = new("tenant-1");

    private static ReadOnlyMemory<byte> BuildFormBody(Dictionary<string, string> fields)
    {
        var pairs = fields.Select(kv =>
            $"{HttpUtility.UrlEncode(kv.Key)}={HttpUtility.UrlEncode(kv.Value)}");
        return Encoding.UTF8.GetBytes(string.Join("&", pairs));
    }

    [Fact]
    public async Task HandleAsync_ShouldParseInboundTextMessage()
    {
        var provider = Substitute.For<ISmsProvider>();
        var handler = new SmsWebhookHandler(provider);

        var body = BuildFormBody(new()
        {
            ["SmsSid"] = "SM123",
            ["From"] = "+15551234567",
            ["To"] = "+15559876543",
            ["Body"] = "Hello from SMS",
            ["NumMedia"] = "0",
        });

        var result = await handler.HandleAsync(body, new Dictionary<string, string>(), Tenant, CancellationToken.None);

        result.Type.Should().Be(WebhookResultType.NewMessage);
        result.Message.Should().NotBeNull();
        result.Message!.From.Address.Should().Be("+15551234567");
        result.Message.ExternalMessageId.Should().Be("SM123");
        result.Message.Content.Blocks.Should().ContainSingle();
    }

    [Fact]
    public async Task HandleAsync_ShouldParseInboundWithMedia()
    {
        var provider = Substitute.For<ISmsProvider>();
        var handler = new SmsWebhookHandler(provider);

        var body = BuildFormBody(new()
        {
            ["SmsSid"] = "SM456",
            ["From"] = "+15551234567",
            ["Body"] = "Check this out",
            ["NumMedia"] = "1",
            ["MediaUrl0"] = "https://api.twilio.com/media/image.jpg",
            ["MediaContentType0"] = "image/jpeg",
        });

        var result = await handler.HandleAsync(body, new Dictionary<string, string>(), Tenant, CancellationToken.None);

        result.Type.Should().Be(WebhookResultType.NewMessage);
        result.Message!.Content.Blocks.Should().HaveCount(2); // text + file
    }

    [Fact]
    public async Task HandleAsync_ShouldStillHandleStatusUpdates()
    {
        var provider = Substitute.For<ISmsProvider>();
        provider.GetStatusAsync("SM789", Arg.Any<CancellationToken>())
            .Returns(SmsDeliveryStatus.Delivered);

        var handler = new SmsWebhookHandler(provider);

        var body = BuildFormBody(new()
        {
            ["MessageSid"] = "SM789",
            ["MessageStatus"] = "delivered",
        });

        var result = await handler.HandleAsync(body, new Dictionary<string, string>(), Tenant, CancellationToken.None);

        result.Type.Should().Be(WebhookResultType.StatusUpdate);
        result.StatusUpdate.Should().NotBeNull();
    }

    [Fact]
    [SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification = "Test validates Twilio's HMAC-SHA1 protocol")]
    public void TwilioSignature_ShouldValidateCorrectly()
    {
        // Manually compute expected signature for a known input
        var authToken = "test_token";
        var url = "https://example.com/webhook";
        var parameters = new Dictionary<string, string>
        {
            ["Body"] = "Hello",
            ["From"] = "+1555",
        };

        // Compute expected
        var builder = new StringBuilder(url);
        foreach (var p in parameters.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            builder.Append(p.Key);
            builder.Append(p.Value);
        }
        var data = Encoding.UTF8.GetBytes(builder.ToString());
        var key = Encoding.UTF8.GetBytes(authToken);
        var hash = System.Security.Cryptography.HMACSHA1.HashData(key, data);
        var expectedSignature = Convert.ToBase64String(hash);

        TwilioSignatureValidator.Validate(authToken, url, parameters, expectedSignature).Should().BeTrue();
        TwilioSignatureValidator.Validate(authToken, url, parameters, "wrong_sig").Should().BeFalse();
    }
}
