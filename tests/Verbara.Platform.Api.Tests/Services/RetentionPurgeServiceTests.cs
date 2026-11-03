using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Audit;
using Verbara.Platform.Billing;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Storage.InMemory;
using Xunit;

namespace Verbara.Platform.Api.Tests.Services;

/// <summary>
/// retention-purges-closed-conversations-only — one purge cycle over a tenant with a conversation retention
/// policy: the open conversation survives however old it is, the one closed before the cutoff goes, and the
/// orphan-message cleanup still runs after the conversation delete.
/// </summary>
public sealed class RetentionPurgeServiceTests
{
    private const string TenantKey = "tenant-retention";
    private static readonly TenantId Tenant = new(TenantKey);
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RunRetentionPurgeAsync_ShouldKeepOpenConversationAndPurgeClosedOne_WhenBothCreatedBeforeCutoff()
    {
        var conversations = new InMemoryConversationStore();
        var open = Make(ConversationState.Active, closedAt: null);
        var closed = Make(ConversationState.Closed, closedAt: Now.AddDays(-120));
        await conversations.SaveAsync(open, CancellationToken.None);
        await conversations.SaveAsync(closed, CancellationToken.None);

        var messages = Substitute.For<IMessageStore>();
        messages.DeleteOrphanedAsync(Tenant, Arg.Any<CancellationToken>()).Returns(3);
        var purgeLog = Substitute.For<IPurgeLogStore>();
        var sut = NewService(conversations, messages, purgeLog);

        await sut.RunRetentionPurgeAsync(CancellationToken.None);

        (await conversations.GetByIdAsync(Tenant, open.ConversationId, CancellationToken.None))
            .Should().NotBeNull("an open conversation is never purged, however old");
        (await conversations.GetByIdAsync(Tenant, closed.ConversationId, CancellationToken.None))
            .Should().BeNull("it closed 120 days ago under a 90-day policy");
        await messages.Received(1).DeleteOrphanedAsync(Tenant, Arg.Any<CancellationToken>());
        await purgeLog.Received(1).SaveAsync(
            Arg.Is<PurgeEntry>(e =>
                e.TenantId == TenantKey &&
                e.EntitiesDeleted["conversations"] == 1 &&
                e.EntitiesDeleted["orphanedMessages"] == 3),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunRetentionPurgeAsync_ShouldPurgeResolvedConversation_WhenIdleSinceBeforeCutoff()
    {
        var conversations = new InMemoryConversationStore();
        var idle = Make(ConversationState.Resolved, closedAt: null);
        idle.UpdatedAt = Now.AddDays(-120);
        var recent = Make(ConversationState.Resolved, closedAt: null);
        recent.UpdatedAt = Now.AddDays(-10);
        await conversations.SaveAsync(idle, CancellationToken.None);
        await conversations.SaveAsync(recent, CancellationToken.None);
        var purgeLog = Substitute.For<IPurgeLogStore>();
        var sut = NewService(conversations, Substitute.For<IMessageStore>(), purgeLog);

        await sut.RunRetentionPurgeAsync(CancellationToken.None);

        (await conversations.GetByIdAsync(Tenant, idle.ConversationId, CancellationToken.None))
            .Should().BeNull("nothing closes a Resolved conversation on its own; idle past the window it is purged");
        (await conversations.GetByIdAsync(Tenant, recent.ConversationId, CancellationToken.None))
            .Should().NotBeNull("it changed 10 days ago under a 90-day policy");
        await purgeLog.Received(1).SaveAsync(
            Arg.Is<PurgeEntry>(e => e.TenantId == TenantKey && e.EntitiesDeleted["conversations"] == 1),
            Arg.Any<CancellationToken>());
    }

    private static RetentionPurgeService NewService(
        IConversationStore conversations, IMessageStore messages, IPurgeLogStore purgeLog)
    {
        var policies = Substitute.For<ITenantRetentionPolicyStore>();
        policies.ListActiveAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new TenantRetentionPolicy { TenantId = TenantKey, ConversationRetentionDays = 90 },
        ]);
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(Now);

        return new RetentionPurgeService(
            policies,
            conversations,
            messages,
            Substitute.For<IAuthEventStore>(),
            Substitute.For<IAuditStore>(),
            Substitute.For<IUsageRecordStore>(),
            purgeLog,
            clock,
            NullLogger<RetentionPurgeService>.Instance,
            new ConfigurationBuilder().Build());
    }

    private static Conversation Make(ConversationState state, DateTimeOffset? closedAt) => new()
    {
        ConversationId = EntityId.New(),
        TenantId = Tenant,
        ContactId = EntityId.New(),
        Channel = ChannelType.WhatsApp,
        State = state,
        CreatedAt = Now.AddDays(-200),
        ClosedAt = closedAt,
    };
}
