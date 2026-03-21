using Asterisk.Platform.Channels.Email;
using Asterisk.Platform.Conversations;
using Asterisk.Platform.Conversations.Stores;
using Asterisk.Platform.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Asterisk.Platform.Channels.Email.Tests;

public class EmailThreadResolverTests
{
    private static readonly TenantId Tenant = new("tenant-1");
    private static readonly EntityId ConversationId = EntityId.From("conv-123");

    private static EmailThreadResolver CreateSut(IMessageStore store) =>
        new(store, NullLogger<EmailThreadResolver>.Instance);

    private static ParsedEmail BuildEmail(
        string messageId = "<new@example.com>",
        string? inReplyTo = null,
        string? references = null,
        string subject = "Test Subject") =>
        new(messageId, inReplyTo, references, "alice@example.com",
            subject, "Body", null, [], DateTimeOffset.UtcNow);

    private static Message BuildMessage(EntityId msgId, EntityId convId, TenantId tenantId) =>
        new()
        {
            MessageId = msgId,
            ConversationId = convId,
            TenantId = tenantId,
            Direction = MessageDirection.Outbound,
            Channel = ChannelType.Email,
            Content = new MessageEnvelope([]),
            DeliveryStatus = MessageDeliveryStatus.Sent,
            CreatedAt = DateTimeOffset.UtcNow,
        };

    [Fact]
    public async Task ResolveAsync_ShouldReturnConversationId_WhenInReplyToMatchesExistingMessage()
    {
        var store = Substitute.For<IMessageStore>();
        var message = BuildMessage(EntityId.From("msg-1"), ConversationId, Tenant);
        store.FindByExternalIdAsync(Tenant, "<prev@example.com>", Arg.Any<CancellationToken>())
            .Returns(message);

        var sut = CreateSut(store);
        var email = BuildEmail(inReplyTo: "<prev@example.com>");

        var result = await sut.ResolveAsync(Tenant, email, CancellationToken.None);

        result.Should().Be(ConversationId);
    }

    [Fact]
    public async Task ResolveAsync_ShouldReturnConversationId_WhenReferencesMatchExistingMessage()
    {
        var store = Substitute.For<IMessageStore>();
        var message = BuildMessage(EntityId.From("msg-2"), ConversationId, Tenant);
        store.FindByExternalIdAsync(Tenant, "<orig@example.com>", Arg.Any<CancellationToken>())
            .Returns(message);

        var sut = CreateSut(store);
        var email = BuildEmail(references: "<orig@example.com> <prev@example.com>");

        var result = await sut.ResolveAsync(Tenant, email, CancellationToken.None);

        result.Should().Be(ConversationId);
    }

    [Fact]
    public async Task ResolveAsync_ShouldReturnNull_WhenNoThreadMatch()
    {
        var store = Substitute.For<IMessageStore>();
        store.FindByExternalIdAsync(Arg.Any<TenantId>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((Message?)null);

        var sut = CreateSut(store);
        var email = BuildEmail(subject: "Brand New Topic");

        var result = await sut.ResolveAsync(Tenant, email, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ResolveAsync_ShouldFallBackToSubject_WhenHeadersDoNotMatch()
    {
        var store = Substitute.For<IMessageStore>();
        var message = BuildMessage(EntityId.From("msg-3"), ConversationId, Tenant);

        // Subject normalized to "HELLO WORLD"
        store.FindByExternalIdAsync(Tenant, "subject:HELLO WORLD", Arg.Any<CancellationToken>())
            .Returns(message);

        var sut = CreateSut(store);
        var email = BuildEmail(subject: "Re: Hello World");

        var result = await sut.ResolveAsync(Tenant, email, CancellationToken.None);

        result.Should().Be(ConversationId);
    }

    [Fact]
    public void NormalizeSubject_ShouldStrip_RePrefix()
    {
        var result = EmailThreadResolver.NormalizeSubject("Re: Hello World");

        result.Should().Be("HELLO WORLD");
    }

    [Fact]
    public void NormalizeSubject_ShouldStrip_MultipleRePrefixes()
    {
        var result = EmailThreadResolver.NormalizeSubject("Re: Re: Fwd: Hello");

        result.Should().Be("HELLO");
    }

    [Fact]
    public void ParseReferences_ShouldReturnAllMessageIds()
    {
        var refs = "<id1@example.com> <id2@example.com> <id3@example.com>";

        var result = EmailThreadResolver.ParseReferences(refs).ToList();

        result.Should().HaveCount(3);
        result[0].Should().Be("<id1@example.com>");
        result[2].Should().Be("<id3@example.com>");
    }
}
