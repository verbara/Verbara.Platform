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
        // Use a zero grace window so a just-rotated replay is treated as genuine reuse
        // (under the default 15s grace window a same-millisecond replay would converge
        // idempotently instead — that behavior is covered by the dedicated grace tests).
        var sut = new RefreshTokenService(_store, rotationGraceWindow: TimeSpan.Zero);

        // Generate and rotate (original becomes revoked)
        var (rawToken, _) = await sut.GenerateAsync("user1", "t1", null, null, CancellationToken.None);
        var rotated = await sut.RotateAsync(rawToken, null, null, CancellationToken.None);
        rotated.Should().NotBeNull();

        // Generate a second active token for same user
        var (_, secondToken) = await sut.GenerateAsync("user1", "t1", null, null, CancellationToken.None);

        // Replay the original (already revoked) token — reuse detection
        var replayResult = await sut.RotateAsync(rawToken, null, null, CancellationToken.None);

        replayResult.Should().BeNull();

        // All tokens for this user should be revoked (including the second one)
        var activeTokens = await _store.GetActiveByUserAsync("t1", "user1", CancellationToken.None);
        activeTokens.Should().BeEmpty();
    }

    [Fact]
    public async Task RotateAsync_ShouldReturnNewTokenIdempotently_WhenJustRotatedTokenReplayedWithinGrace()
    {
        // Default grace window (15s). Generate → rotate once → replay the ORIGINAL raw
        // token with no clock advance, simulating a second browser tab presenting the
        // same just-rotated token within the grace window.
        var (rawToken, _) = await _sut.GenerateAsync("user1", "t1", "127.0.0.1", "Agent", CancellationToken.None);

        var firstRotation = await _sut.RotateAsync(rawToken, null, null, CancellationToken.None);
        firstRotation.Should().NotBeNull();

        // Replay within grace → should converge idempotently with a fresh chained token,
        // NOT family-revoke.
        var replay = await _sut.RotateAsync(rawToken, "10.0.0.2", "TabTwo", CancellationToken.None);

        replay.Should().NotBeNull();
        var (replayRaw, replayStored) = replay!.Value;
        replayRaw.Should().NotBeNullOrWhiteSpace();
        replayStored.UserId.Should().Be("user1");
        replayStored.TenantId.Should().Be("t1");

        // Family must NOT be revoked — at least one active token remains.
        var activeTokens = await _store.GetActiveByUserAsync("t1", "user1", CancellationToken.None);
        activeTokens.Should().NotBeEmpty();
    }

    [Fact]
    public async Task RotateAsync_ShouldRevokeFamily_WhenRevokedTokenReplayedAfterGraceWindow()
    {
        // Zero grace window → a just-rotated replay is past the grace immediately and is
        // treated as genuine reuse, family-revoking everything.
        var sut = new RefreshTokenService(_store, rotationGraceWindow: TimeSpan.Zero);

        var (rawToken, _) = await sut.GenerateAsync("user1", "t1", null, null, CancellationToken.None);
        var rotated = await sut.RotateAsync(rawToken, null, null, CancellationToken.None);
        rotated.Should().NotBeNull();

        var replay = await sut.RotateAsync(rawToken, null, null, CancellationToken.None);

        replay.Should().BeNull();

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
