using Asterisk.Platform.Channels.Video;
using Asterisk.Platform.Core;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Asterisk.Platform.Channels.Video.Tests;

public class VideoSessionManagerTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly EntityId ConversationA = EntityId.From("conv-1");
    private static readonly EntityId AgentA = EntityId.From("agent-1");

    private static VideoSessionManager CreateManager(
        IVideoTransport? transport = null,
        TimeSpan? sessionTimeout = null)
    {
        var opts = new VideoOptions
        {
            SessionTimeout = sessionTimeout ?? TimeSpan.FromMinutes(60),
        };
        return new VideoSessionManager(
            transport ?? Substitute.For<IVideoTransport>(),
            Options.Create(opts));
    }

    private static VideoSessionRequest MakeRequest(EntityId? conversationId = null) =>
        new(TenantA, conversationId ?? ConversationA, AgentA, "customer-abc");

    private static VideoSession MakeSession(string sessionId = "session-1", DateTimeOffset? createdAt = null) =>
        new(sessionId, "wss://signaling.example.com", "cust-token", "agent-token",
            createdAt ?? DateTimeOffset.UtcNow);

    [Fact]
    public async Task CreateAsync_ShouldReturnSession_WhenTransportSucceeds()
    {
        var transport = Substitute.For<IVideoTransport>();
        var expected = MakeSession();
        transport.CreateSessionAsync(Arg.Any<VideoSessionRequest>(), Arg.Any<CancellationToken>())
            .Returns(expected);

        var manager = CreateManager(transport);
        var session = await manager.CreateAsync(MakeRequest(), CancellationToken.None);

        session.Should().Be(expected);
    }

    [Fact]
    public async Task CreateAsync_ShouldMakeSessionRetrievable_WhenCreated()
    {
        var transport = Substitute.For<IVideoTransport>();
        var conversationId = EntityId.From("conv-retrievable");
        var expected = MakeSession("session-retrievable");
        transport.CreateSessionAsync(Arg.Any<VideoSessionRequest>(), Arg.Any<CancellationToken>())
            .Returns(expected);

        var manager = CreateManager(transport);
        await manager.CreateAsync(MakeRequest(conversationId), CancellationToken.None);
        var retrieved = await manager.GetActiveSessionAsync(conversationId);

        retrieved.Should().Be(expected);
    }

    [Fact]
    public async Task EndAsync_ShouldRemoveSession_WhenSessionExists()
    {
        var transport = Substitute.For<IVideoTransport>();
        var conversationId = EntityId.From("conv-end");
        var session = MakeSession("session-end");
        transport.CreateSessionAsync(Arg.Any<VideoSessionRequest>(), Arg.Any<CancellationToken>())
            .Returns(session);

        var manager = CreateManager(transport);
        await manager.CreateAsync(MakeRequest(conversationId), CancellationToken.None);
        await manager.EndAsync("session-end", CancellationToken.None);

        var result = await manager.GetActiveSessionAsync(conversationId);
        result.Should().BeNull();
    }

    [Fact]
    public async Task EndAsync_ShouldDelegateToTransport_WhenCalled()
    {
        var transport = Substitute.For<IVideoTransport>();
        var session = MakeSession("session-transport");
        transport.CreateSessionAsync(Arg.Any<VideoSessionRequest>(), Arg.Any<CancellationToken>())
            .Returns(session);

        var manager = CreateManager(transport);
        await manager.CreateAsync(MakeRequest(), CancellationToken.None);
        await manager.EndAsync("session-transport", CancellationToken.None);

        await transport.Received(1).EndSessionAsync("session-transport", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetActiveSessionAsync_ShouldReturnNull_WhenNoSessionExists()
    {
        var manager = CreateManager();

        var result = await manager.GetActiveSessionAsync(EntityId.From("conv-none"));

        result.Should().BeNull();
    }

    [Fact]
    public async Task CleanupExpiredAsync_ShouldRemoveExpiredSessions_WhenTimeoutElapsed()
    {
        var transport = Substitute.For<IVideoTransport>();
        var createdAt = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var session1 = MakeSession("session-exp-1", createdAt);
        var session2 = MakeSession("session-exp-2", createdAt);
        var callCount = 0;
        transport.CreateSessionAsync(Arg.Any<VideoSessionRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return callCount == 1 ? session1 : session2;
            });

        var manager = CreateManager(transport, TimeSpan.FromMinutes(60));
        await manager.CreateAsync(MakeRequest(EntityId.From("conv-e1")), CancellationToken.None);
        await manager.CreateAsync(MakeRequest(EntityId.From("conv-e2")), CancellationToken.None);

        var later = createdAt.AddMinutes(61);
        var removed = await manager.CleanupExpiredAsync(later);

        removed.Should().Be(2);
        (await manager.GetActiveSessionAsync(EntityId.From("conv-e1"))).Should().BeNull();
        (await manager.GetActiveSessionAsync(EntityId.From("conv-e2"))).Should().BeNull();
    }

    [Fact]
    public async Task CleanupExpiredAsync_ShouldNotRemoveActiveSessions_WhenNotExpired()
    {
        var transport = Substitute.For<IVideoTransport>();
        var createdAt = DateTimeOffset.UtcNow;
        var session = MakeSession("session-active", createdAt);
        transport.CreateSessionAsync(Arg.Any<VideoSessionRequest>(), Arg.Any<CancellationToken>())
            .Returns(session);

        var manager = CreateManager(transport, TimeSpan.FromMinutes(60));
        var conversationId = EntityId.From("conv-active");
        await manager.CreateAsync(MakeRequest(conversationId), CancellationToken.None);

        var removed = await manager.CleanupExpiredAsync(createdAt.AddMinutes(30));

        removed.Should().Be(0);
        (await manager.GetActiveSessionAsync(conversationId)).Should().NotBeNull();
    }

    [Fact]
    public void IsExpired_ShouldReturnTrue_WhenSessionExceedsTimeout()
    {
        var manager = CreateManager(sessionTimeout: TimeSpan.FromMinutes(60));
        var createdAt = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var session = MakeSession("s", createdAt);

        var result = manager.IsExpired(session, createdAt.AddMinutes(61));

        result.Should().BeTrue();
    }

    [Fact]
    public void IsExpired_ShouldReturnFalse_WhenSessionIsWithinTimeout()
    {
        var manager = CreateManager(sessionTimeout: TimeSpan.FromMinutes(60));
        var createdAt = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var session = MakeSession("s", createdAt);

        var result = manager.IsExpired(session, createdAt.AddMinutes(59));

        result.Should().BeFalse();
    }
}
