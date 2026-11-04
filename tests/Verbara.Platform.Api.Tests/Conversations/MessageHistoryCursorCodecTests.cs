using System.Buffers.Text;
using System.Text;
using Verbara.Platform.Api.Endpoints;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;

namespace Verbara.Platform.Api.Tests.Conversations;

/// <summary>
/// contact-history-opens-by-assignment task 2.4 (design D10): the history cursor is base64url of
/// <c>"&lt;createdAt UTC ticks&gt;:&lt;messageId&gt;"</c>, reads back the exact position it was built from (any offset,
/// a message id holding a colon), and refuses every value it did not write.
/// </summary>
public sealed class MessageHistoryCursorCodecTests
{
    private static Message MessageAt(DateTimeOffset at, string id) => new()
    {
        MessageId = EntityId.From(id),
        ConversationId = EntityId.From("c-1"),
        TenantId = new TenantId("t-1"),
        Direction = MessageDirection.Inbound,
        Channel = ChannelType.WhatsApp,
        Content = new MessageEnvelope([new TextBlock("hi")]),
        DeliveryStatus = MessageDeliveryStatus.Delivered,
        CreatedAt = at,
    };

    private static string B64(string text) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void Encode_ShouldWriteBase64UrlOfUtcTicksAndTheMessageId_WhenGivenThePagesLastItem()
    {
        var at = new DateTimeOffset(2026, 10, 28, 9, 15, 0, TimeSpan.FromHours(-5)).AddTicks(7);

        var cursor = MessageHistoryCursorCodec.Encode(MessageAt(at, "m-0001"));

        Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor))
            .Should().Be($"{at.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture)}:m-0001");
        cursor.Should().NotContainAny("+", "/", "=");
    }

    [Theory]
    [InlineData("m-0001")]
    [InlineData("urn:verbara:m:42")]
    public void TryDecode_ShouldReadBackTheExactPosition_WhenTheCursorWasEncoded(string messageId)
    {
        var at = new DateTimeOffset(2026, 10, 28, 9, 15, 0, TimeSpan.FromHours(3)).AddTicks(123);

        var read = MessageHistoryCursorCodec.TryDecode(MessageHistoryCursorCodec.Encode(MessageAt(at, messageId)), out var cursor);

        read.Should().BeTrue();
        cursor.CreatedAt.UtcTicks.Should().Be(at.UtcTicks);
        cursor.CreatedAt.Offset.Should().Be(TimeSpan.Zero);
        cursor.MessageId.Value.Should().Be(messageId);
    }

    public static TheoryData<string> Forged() => new()
    {
        "",
        "not-a-cursor",
        "%%%",
        "abc def",
        B64("abc:m-1"),
        B64("12345"),
        B64(":m-1"),
        B64("12345:"),
        B64("-5:m-1"),
        B64("+5:m-1"),
        B64(" 5:m-1"),
        B64("1e5:m-1"),
        B64("99999999999999999999:m-1"),
        B64($"{(DateTime.MaxValue.Ticks + 1L).ToString(System.Globalization.CultureInfo.InvariantCulture)}:m-1"),
    };

    [Theory]
    [MemberData(nameof(Forged))]
    public void TryDecode_ShouldRefuse_WhenTheValueIsNotACursorItWrote(string forged)
    {
        MessageHistoryCursorCodec.TryDecode(forged, out _).Should().BeFalse($"'{forged}' names no position");
    }
}
