using Verbara.Platform.Api.Services;
using Verbara.Platform.Identity;

namespace Verbara.Platform.Api.Tests.Services;

public sealed class RefreshTokenServiceTests
{
    private readonly InMemoryRefreshTokenStore _store = new();
    private readonly RefreshTokenService _sut;

    public RefreshTokenServiceTests()
    {
        _sut = new RefreshTokenService(_store);
    }

    [Fact]
    public async Task GenerateAsync_ShouldReturnRawTokenAndStoredToken()
    {
        var (rawToken, storedToken) = await _sut.GenerateAsync("user1", "t1", "127.0.0.1", "TestAgent", CancellationToken.None);

        rawToken.Should().NotBeNullOrWhiteSpace();
        storedToken.UserId.Should().Be("user1");
        storedToken.TenantId.Should().Be("t1");
        storedToken.IpAddress.Should().Be("127.0.0.1");
        storedToken.UserAgent.Should().Be("TestAgent");
        storedToken.TokenHash.Should().Be(RefreshTokenService.HashToken(rawToken));
        storedToken.IsRevoked.Should().BeFalse();
        storedToken.ExpiresAt.Should().BeAfter(DateTimeOffset.UtcNow);

        // Verify persisted in store
        var fromStore = await _store.GetByHashAsync(storedToken.TokenHash, CancellationToken.None);
        fromStore.Should().NotBeNull();
    }

    [Fact]
    public async Task GenerateAsync_ShouldSetExpiryToAbsolute24Hours_WhenCalled()
    {
        var before = DateTimeOffset.UtcNow;

        var (_, token) = await _sut.GenerateAsync("user1", "t1", null, null, CancellationToken.None);

        token.ExpiresAt.Should().BeCloseTo(before.AddHours(24), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task RotateAsync_ShouldReturnNewToken_WhenTokenIsValid()
    {
        var (rawToken, original) = await _sut.GenerateAsync("user1", "t1", "127.0.0.1", "Agent", CancellationToken.None);

        var result = await _sut.RotateAsync(rawToken, "10.0.0.1", "NewAgent", CancellationToken.None);

        result.Should().NotBeNull();
        var (newRaw, newStored) = result!.Value;
        newRaw.Should().NotBeNullOrWhiteSpace();
        newRaw.Should().NotBe(rawToken);
        newStored.UserId.Should().Be("user1");
        newStored.TenantId.Should().Be("t1");
        newStored.IpAddress.Should().Be("10.0.0.1");

        // Original should be revoked
        var oldFromStore = await _store.GetByHashAsync(original.TokenHash, CancellationToken.None);
        oldFromStore!.IsRevoked.Should().BeTrue();
        oldFromStore.ReplacedBy.Should().Be(newStored.TokenId);
    }

    [Fact]
    public async Task RotateAsync_ShouldReturnNull_WhenTokenDoesNotExist()
    {
        var result = await _sut.RotateAsync("nonexistent-token", null, null, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task RotateAsync_ShouldRevokeAllUserTokens_WhenRevokedTokenIsReused()
    {
        // Generate and rotate (original becomes revoked)
        var (rawToken, _) = await _sut.GenerateAsync("user1", "t1", null, null, CancellationToken.None);
        var rotated = await _sut.RotateAsync(rawToken, null, null, CancellationToken.None);
        rotated.Should().NotBeNull();

        // Generate a second active token for same user
        var (_, secondToken) = await _sut.GenerateAsync("user1", "t1", null, null, CancellationToken.None);

        // Replay the original (already revoked) token — reuse detection
        var replayResult = await _sut.RotateAsync(rawToken, null, null, CancellationToken.None);

        replayResult.Should().BeNull();

        // All tokens for this user should be revoked (including the second one)
        var activeTokens = await _store.GetActiveByUserAsync("t1", "user1", CancellationToken.None);
        activeTokens.Should().BeEmpty();
    }

    [Fact]
    public async Task RevokeAsync_ShouldMarkTokenAsRevoked()
    {
        var (rawToken, storedToken) = await _sut.GenerateAsync("user1", "t1", null, null, CancellationToken.None);

        await _sut.RevokeAsync(rawToken, CancellationToken.None);

        var fromStore = await _store.GetByHashAsync(storedToken.TokenHash, CancellationToken.None);
        fromStore!.IsRevoked.Should().BeTrue();
        fromStore.ReplacedBy.Should().BeNull();
    }
}
