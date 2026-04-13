using Asterisk.Platform.Api.Services;
using Asterisk.Platform.Core;
using Asterisk.Platform.Identity;
using NSubstitute;

namespace Asterisk.Platform.Api.Tests.Services;

public sealed class SessionServiceTests
{
    private readonly InMemoryRefreshTokenStore _tokenStore = new();
    private readonly IAuthEventStore _eventStore = Substitute.For<IAuthEventStore>();
    private readonly IUserStore _userStore = Substitute.For<IUserStore>();
    private readonly SessionService _sut;

    public SessionServiceTests()
    {
        var authEvents = new AuthEventService(_eventStore);
        _sut = new SessionService(_tokenStore, _userStore, authEvents);
    }

    private static User MakeUser(string userId, string tenantId, string email = "u1@example.com", string displayName = "User One") =>
        new User
        {
            UserId = EntityId.From(userId),
            TenantId = new TenantId(tenantId),
            Email = email,
            DisplayName = displayName,
            Role = UserRole.Agent,
            Status = UserStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
        };

    [Fact]
    public async Task ListActiveSessionsAsync_ShouldReturnActiveSessions()
    {
        var user = MakeUser("u1", "tenant1");
        _userStore.GetByIdAsync(new TenantId("tenant1"), EntityId.From("u1"), Arg.Any<CancellationToken>())
            .Returns(user);

        await _tokenStore.SaveAsync(new RefreshToken
        {
            TokenId = "t1",
            UserId = "u1",
            TenantId = "tenant1",
            TokenHash = "hash1",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
            CreatedAt = DateTimeOffset.UtcNow,
            IpAddress = "1.2.3.4",
            UserAgent = "Browser/1.0",
        }, CancellationToken.None);

        var sessions = await _sut.ListActiveSessionsAsync("tenant1", "u1", CancellationToken.None);

        sessions.Should().HaveCount(1);
        sessions[0].SessionId.Should().Be("t1");
        sessions[0].UserId.Should().Be("u1");
        sessions[0].UserEmail.Should().Be("u1@example.com");
        sessions[0].UserDisplayName.Should().Be("User One");
        sessions[0].IpAddress.Should().Be("1.2.3.4");
        sessions[0].UserAgent.Should().Be("Browser/1.0");
    }

    [Fact]
    public async Task RevokeSessionAsync_ShouldRevokeTokenAndLogEvent()
    {
        await _tokenStore.SaveAsync(new RefreshToken
        {
            TokenId = "t1",
            UserId = "u1",
            TenantId = "tenant1",
            TokenHash = "hash1",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
            CreatedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        await _sut.RevokeSessionAsync("tenant1", "u1", "t1", "10.0.0.1", "Agent", CancellationToken.None);

        var remaining = await _tokenStore.GetActiveByUserAsync("tenant1", "u1", CancellationToken.None);
        remaining.Should().BeEmpty();

        await _eventStore.Received(1).SaveAsync(
            Arg.Is<AuthEvent>(e => e.EventType == AuthEventTypes.SessionRevoked),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListActiveSessionsAsync_ShouldReturnEmpty_WhenNoSessions()
    {
        var user = MakeUser("u1", "tenant1");
        _userStore.GetByIdAsync(new TenantId("tenant1"), EntityId.From("u1"), Arg.Any<CancellationToken>())
            .Returns(user);

        var sessions = await _sut.ListActiveSessionsAsync("tenant1", "u1", CancellationToken.None);

        sessions.Should().BeEmpty();
    }
}
