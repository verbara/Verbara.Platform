using System.Globalization;
using System.Security.Claims;
using Verbara.Platform.Identity.Auth;
using NSubstitute;

namespace Verbara.Platform.Identity.Tests.Auth;

/// <summary>
/// The revocation rule both hosts share: what a revocation writes, how long it is kept, and how a
/// token is judged against it — including that a store failure is thrown, never read as "not revoked".
/// </summary>
public sealed class ImpersonationTokenRevocationTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset TokenExpiresAt = Start.AddMinutes(30);

    [Fact]
    public async Task RevokeAsync_ShouldKeepEntryPastExpiry_ByMaxClockSkew()
    {
        // Validators still accept a token up to their clock skew past its exp. Kept only until exp,
        // a revoked token would validate again for that long.
        var clock = new SettableClock(Start);
        var cache = new InMemoryJtiRevocationCache(clock);

        await ImpersonationTokenRevocation.RevokeAsync(cache, "jti-1", TokenExpiresAt, CancellationToken.None);

        clock.Now = TokenExpiresAt + ImpersonationTokenRevocation.MaxClockSkew - TimeSpan.FromSeconds(1);
        (await cache.IsRevokedAsync("jti-1", CancellationToken.None)).Should().BeTrue(
            because: "the token is still inside the skew a validator grants past its exp");

        clock.Now = TokenExpiresAt + ImpersonationTokenRevocation.MaxClockSkew + TimeSpan.FromSeconds(1);
        (await cache.IsRevokedAsync("jti-1", CancellationToken.None)).Should().BeFalse(
            because: "past exp plus the skew no validator accepts the token, so the entry can go");
    }

    [Fact]
    public async Task RevokeAsync_ShouldRevokeTheTokensOwnJtiUntilItsExpiry_WhenGivenItsPrincipal()
    {
        var cache = Substitute.For<IJtiRevocationCache>();
        var principal = ImpersonationPrincipal(tokenId: "jti-own", TokenExpiresAt);

        await ImpersonationTokenRevocation.RevokeAsync(cache, principal, CancellationToken.None);

        await cache.Received(1).RevokeAsync(
            "jti-own", TokenExpiresAt + ImpersonationTokenRevocation.MaxClockSkew, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RevokeAsync_ShouldLeaveTheCacheAlone_WhenPrincipalNamesNoTokenId()
    {
        var cache = Substitute.For<IJtiRevocationCache>();

        await ImpersonationTokenRevocation.RevokeAsync(
            cache, ImpersonationPrincipal(tokenId: null, TokenExpiresAt), CancellationToken.None);

        await cache.DidNotReceiveWithAnyArgs().RevokeAsync(default!, default, default);
    }

    [Fact]
    public async Task RevokeAsync_ShouldThrow_WhenTheCacheFails()
    {
        var act = async () => await ImpersonationTokenRevocation.RevokeAsync(
            new UnreachableCache(), "jti-1", TokenExpiresAt, CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>(
            because: "the caller must learn the token was not revoked, so it does not report the session closed");
    }

    [Fact]
    public async Task RevokeAsync_ShouldRefuse_WhenTokenIdIsEmpty()
    {
        var act = async () => await ImpersonationTokenRevocation.RevokeAsync(
            new InMemoryJtiRevocationCache(), "", TokenExpiresAt, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task IsRevokedAsync_ShouldBeTrue_WhenTheTokenWasRevoked()
    {
        var cache = new InMemoryJtiRevocationCache();
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(30);
        await ImpersonationTokenRevocation.RevokeAsync(cache, "jti-revoked", expiresAt, CancellationToken.None);

        var revoked = await ImpersonationTokenRevocation.IsRevokedAsync(
            cache, ImpersonationPrincipal("jti-revoked", expiresAt), CancellationToken.None);

        revoked.Should().BeTrue();
    }

    [Fact]
    public async Task IsRevokedAsync_ShouldBeFalse_WhenAnotherTokenWasRevoked()
    {
        var cache = new InMemoryJtiRevocationCache();
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(30);
        await ImpersonationTokenRevocation.RevokeAsync(cache, "jti-other", expiresAt, CancellationToken.None);

        var revoked = await ImpersonationTokenRevocation.IsRevokedAsync(
            cache, ImpersonationPrincipal("jti-mine", expiresAt), CancellationToken.None);

        revoked.Should().BeFalse();
    }

    [Fact]
    public async Task IsRevokedAsync_ShouldTreatTheTokenAsRevoked_WhenItNamesNoTokenId()
    {
        // Every impersonation token Platform.Api mints carries a jti; one without could never be revoked.
        var cache = Substitute.For<IJtiRevocationCache>();

        var revoked = await ImpersonationTokenRevocation.IsRevokedAsync(
            cache, ImpersonationPrincipal(tokenId: null, TokenExpiresAt), CancellationToken.None);

        revoked.Should().BeTrue();
        await cache.DidNotReceiveWithAnyArgs().IsRevokedAsync(default!, default);
    }

    [Fact]
    public async Task IsRevokedAsync_ShouldThrow_WhenTheCacheFails()
    {
        var act = async () => await ImpersonationTokenRevocation.IsRevokedAsync(
            new UnreachableCache(), ImpersonationPrincipal("jti-1", TokenExpiresAt), CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>(
            because: "an unanswered lookup must fail the request, never admit a token that may be revoked");
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", false)]
    [InlineData("false", false)]
    [InlineData(null, false)]
    public void IsImpersonation_ShouldMatchTheImpersonationClaimExactly(string? claimValue, bool expected)
    {
        var claims = new List<Claim> { new("sub", "user-1") };
        if (claimValue is not null)
            claims.Add(new Claim("impersonation", claimValue));

        ImpersonationTokenRevocation.IsImpersonation(new ClaimsPrincipal(new ClaimsIdentity(claims, "test")))
            .Should().Be(expected);
    }

    private static ClaimsPrincipal ImpersonationPrincipal(string? tokenId, DateTimeOffset expiresAt)
    {
        var claims = new List<Claim>
        {
            new("sub", "impersonator-1"),
            new("tid", "target-tenant"),
            new("impersonation", "true"),
            new("exp", expiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
        };
        if (tokenId is not null)
            claims.Add(new Claim("jti", tokenId));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    /// <summary>Fails every call, as the Redis-backed cache does when Redis cannot be reached.</summary>
    private sealed class UnreachableCache : IJtiRevocationCache
    {
        public ValueTask<bool> IsRevokedAsync(string jti, CancellationToken ct) =>
            throw new TimeoutException("store unreachable");

        public ValueTask RevokeAsync(string jti, DateTimeOffset expiresAt, CancellationToken ct) =>
            throw new TimeoutException("store unreachable");
    }

    private sealed class SettableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
