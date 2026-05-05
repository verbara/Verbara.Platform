using Verbara.Platform.Channels.WebChat;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Verbara.Platform.Channels.WebChat.Tests;

public class WebChatSessionManagerTests
{
    private static WebChatSessionManager CreateManager(TimeSpan? sessionTimeout = null)
    {
        var opts = new WebChatOptions
        {
            SessionTimeout = sessionTimeout ?? TimeSpan.FromMinutes(30),
        };
        return new WebChatSessionManager(Options.Create(opts));
    }

    private static IClock MakeClock(DateTimeOffset? at = null)
    {
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(at ?? DateTimeOffset.UtcNow);
        return clock;
    }

    private static TenantId MakeTenant() => new("tenant-1");
    private static EntityId MakeConversation() => EntityId.From("conv-1");
    private static ChannelAddress MakeAddress() => new(ChannelType.WebChat, "visitor-abc");

    [Fact]
    public async Task ConnectAsync_ShouldCreateSession_WhenCalled()
    {
        var manager = CreateManager();
        var clock = MakeClock();

        var sessionId = await manager.ConnectAsync(MakeTenant(), MakeConversation(), MakeAddress(), clock);
        var session = await manager.GetSessionAsync(sessionId);

        session.Should().NotBeNull();
        session!.IsConnected.Should().BeTrue();
        session.SessionId.Should().Be(sessionId);
    }

    [Fact]
    public async Task ConnectAsync_ShouldReturnUniqueToken_WhenCalledTwice()
    {
        var manager = CreateManager();
        var clock = MakeClock();

        var sessionId1 = await manager.ConnectAsync(MakeTenant(), MakeConversation(), MakeAddress(), clock);
        var sessionId2 = await manager.ConnectAsync(MakeTenant(), MakeConversation(), MakeAddress(), clock);

        sessionId1.Should().NotBe(sessionId2);
    }

    [Fact]
    public async Task ConnectAsync_ShouldStoreCorrectMetadata_WhenCalled()
    {
        var manager = CreateManager();
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var clock = MakeClock(now);
        var tenant = MakeTenant();
        var conversationId = MakeConversation();
        var address = MakeAddress();

        var sessionId = await manager.ConnectAsync(tenant, conversationId, address, clock);
        var session = await manager.GetSessionAsync(sessionId);

        session!.TenantId.Should().Be(tenant);
        session.ConversationId.Should().Be(conversationId);
        session.CustomerAddress.Should().Be(address);
        session.CreatedAt.Should().Be(now);
        session.LastActivityAt.Should().Be(now);
    }

    [Fact]
    public async Task DisconnectAsync_ShouldMarkSessionInactive_WhenSessionExists()
    {
        var manager = CreateManager();
        var clock = MakeClock();
        var sessionId = await manager.ConnectAsync(MakeTenant(), MakeConversation(), MakeAddress(), clock);

        await manager.DisconnectAsync(sessionId);
        var session = await manager.GetSessionAsync(sessionId);

        session!.IsConnected.Should().BeFalse();
    }

    [Fact]
    public async Task DisconnectAsync_ShouldNotThrow_WhenSessionDoesNotExist()
    {
        var manager = CreateManager();

        var act = async () => await manager.DisconnectAsync("unknown-session");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ReconnectAsync_ShouldReactivateSession_WhenSessionExistsAndNotExpired()
    {
        var manager = CreateManager();
        var now = DateTimeOffset.UtcNow;
        var clock = MakeClock(now);
        var sessionId = await manager.ConnectAsync(MakeTenant(), MakeConversation(), MakeAddress(), clock);
        await manager.DisconnectAsync(sessionId);

        var result = await manager.ReconnectAsync(sessionId, clock);
        var session = await manager.GetSessionAsync(sessionId);

        result.Should().BeTrue();
        session!.IsConnected.Should().BeTrue();
    }

    [Fact]
    public async Task ReconnectAsync_ShouldReturnFalse_WhenSessionDoesNotExist()
    {
        var manager = CreateManager();
        var clock = MakeClock();

        var result = await manager.ReconnectAsync("unknown-session", clock);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task ReconnectAsync_ShouldReturnFalse_WhenSessionIsExpired()
    {
        var manager = CreateManager(sessionTimeout: TimeSpan.FromMinutes(30));
        var createdAt = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var creationClock = MakeClock(createdAt);
        var sessionId = await manager.ConnectAsync(MakeTenant(), MakeConversation(), MakeAddress(), creationClock);
        await manager.DisconnectAsync(sessionId);

        // Reconnect 31 minutes later (past timeout)
        var expiredAt = createdAt.AddMinutes(31);
        var expiredClock = MakeClock(expiredAt);
        var result = await manager.ReconnectAsync(sessionId, expiredClock);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task CleanupExpiredAsync_ShouldRemoveExpiredSessions_WhenTimeoutElapsed()
    {
        var manager = CreateManager(sessionTimeout: TimeSpan.FromMinutes(30));
        var createdAt = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var creationClock = MakeClock(createdAt);

        var sessionId1 = await manager.ConnectAsync(MakeTenant(), MakeConversation(), MakeAddress(), creationClock);
        var sessionId2 = await manager.ConnectAsync(MakeTenant(), MakeConversation(), MakeAddress(), creationClock);

        // Advance 31 minutes — both sessions are now expired
        var laterClock = MakeClock(createdAt.AddMinutes(31));
        var removed = await manager.CleanupExpiredAsync(laterClock);

        removed.Should().Be(2);
        (await manager.GetSessionAsync(sessionId1)).Should().BeNull();
        (await manager.GetSessionAsync(sessionId2)).Should().BeNull();
    }

    [Fact]
    public async Task CleanupExpiredAsync_ShouldNotRemoveActiveSessions_WhenNotExpired()
    {
        var manager = CreateManager(sessionTimeout: TimeSpan.FromMinutes(30));
        var clock = MakeClock();
        var sessionId = await manager.ConnectAsync(MakeTenant(), MakeConversation(), MakeAddress(), clock);

        var removed = await manager.CleanupExpiredAsync(clock);

        removed.Should().Be(0);
        (await manager.GetSessionAsync(sessionId)).Should().NotBeNull();
    }

    [Fact]
    public async Task TouchAsync_ShouldUpdateLastActivityAt_WhenSessionExists()
    {
        var manager = CreateManager();
        var original = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var updated = original.AddMinutes(10);
        var creationClock = MakeClock(original);
        var touchClock = MakeClock(updated);

        var sessionId = await manager.ConnectAsync(MakeTenant(), MakeConversation(), MakeAddress(), creationClock);
        await manager.TouchAsync(sessionId, touchClock);
        var session = await manager.GetSessionAsync(sessionId);

        session!.LastActivityAt.Should().Be(updated);
    }

    [Fact]
    public async Task TouchAsync_ShouldNotThrow_WhenSessionDoesNotExist()
    {
        var manager = CreateManager();
        var clock = MakeClock();

        var act = async () => await manager.TouchAsync("unknown-session", clock);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task IsExpired_ShouldReturnTrue_WhenSessionExceedsTimeout()
    {
        var manager = CreateManager(sessionTimeout: TimeSpan.FromMinutes(30));
        var createdAt = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var creationClock = MakeClock(createdAt);
        var sessionId = await manager.ConnectAsync(MakeTenant(), MakeConversation(), MakeAddress(), creationClock);
        var session = await manager.GetSessionAsync(sessionId);

        var expiredClock = MakeClock(createdAt.AddMinutes(31));
        var result = manager.IsExpired(session!, expiredClock);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsExpired_ShouldReturnFalse_WhenSessionIsWithinTimeout()
    {
        var manager = CreateManager(sessionTimeout: TimeSpan.FromMinutes(30));
        var createdAt = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var creationClock = MakeClock(createdAt);
        var sessionId = await manager.ConnectAsync(MakeTenant(), MakeConversation(), MakeAddress(), creationClock);
        var session = await manager.GetSessionAsync(sessionId);

        var withinClock = MakeClock(createdAt.AddMinutes(29));
        var result = manager.IsExpired(session!, withinClock);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task GetSessionAsync_ShouldReturnNull_WhenSessionDoesNotExist()
    {
        var manager = CreateManager();

        var session = await manager.GetSessionAsync("nonexistent-id");

        session.Should().BeNull();
    }
}
