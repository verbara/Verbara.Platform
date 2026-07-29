using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Verbara.Platform.Api.Endpoints;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Identity.Mfa;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OtpNet;
using Xunit;

namespace Verbara.Platform.Api.Tests.Mfa;

/// <summary>
/// Cross-seam regression suite for recovery-code redemption
/// (openspec <c>fix-recovery-code-redemption</c>, spec requirement
/// "The regression suite crosses the mint-to-redeem seam").
/// </summary>
/// <remarks>
/// <para>
/// The defect this suite pins shipped because every pre-existing test was a closed
/// loop inside ONE digest family: <c>MfaServiceTests</c> hashed with BCrypt and
/// verified with BCrypt; <c>RecoveryCodeServiceTests</c> hashed with salted SHA-256
/// and verified with salted SHA-256; and the one HTTP test that minted through the
/// wizard threw the plaintext codes away. Nothing ever minted through one endpoint
/// and redeemed through the other, so a redemption path that only understood BCrypt
/// looked fully covered while returning HTTP 500 in production.
/// </para>
/// <para>
/// Every test here therefore mints through the REAL endpoint over HTTP, keeps the
/// plaintext the endpoint handed back, and redeems it through the REAL
/// <c>POST /api/v1/auth/mfa/verify</c>. No test re-hashes a code by hand: doing so
/// would reconstruct the very same single-family closed loop that hid the bug.
/// </para>
/// <para>
/// The MFA challenge token is minted directly through
/// <see cref="AuthEndpoints.GenerateMfaChallengeTokenAndStoreAsync"/> — the same
/// internal seam <c>OidcEndpoints</c> uses — because the seeded fixture user
/// authenticates with an API key and has no password login to drive
/// <c>POST /auth/login</c> with. The redemption half, which is what regressed, still
/// crosses the full HTTP pipeline.
/// </para>
/// <para>
/// <see cref="AuthenticatedPlatformApiFactory"/> backs <c>IUserStore</c> with a
/// substitute that hands out ONE mutable <see cref="User"/> instance, so state
/// written by a mint request is visible to the redemption request — and leaks
/// between tests. <see cref="ResetUserAsync"/> restores a known baseline at the top
/// of every test.
/// </para>
/// </remarks>
public sealed class MfaRecoveryCodeRedemptionTests : IClassFixture<AuthenticatedPlatformApiFactory>
{
    private const string TestPassword = "CurrentPassword123!";

    private readonly AuthenticatedPlatformApiFactory _factory;
    private readonly HttpClient _authClient;
    private readonly HttpClient _anonClient;

    public MfaRecoveryCodeRedemptionTests(AuthenticatedPlatformApiFactory factory)
    {
        _factory = factory;
        _authClient = factory.CreateAuthenticatedClient();

        // Redemption is anonymous — the challenge token is the only credential.
        // The tenant header is still sent so TenantResolutionMiddleware gives the
        // request its own rate-limit partition instead of the shared bucket.
        _anonClient = factory.CreateClient();
        _anonClient.DefaultRequestHeaders.Add("X-Tenant-Id", AuthenticatedPlatformApiFactory.TestTenantId);
    }

    // ─── 4.2 — one end-to-end mint→redeem test per mint path ────────────────

    [Fact]
    public async Task MfaVerify_ShouldReturn200_WhenRedeemingCodeMintedByAuthMfaSetup()
    {
        // Mint path 1 of 4 — POST /auth/mfa/setup, BCrypt cost-10 digests.
        await ResetUserAsync();

        var codes = await MintViaAuthMfaSetupAsync();

        var response = await RedeemAsync(codes[0]);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            because: "a recovery code returned by POST /auth/mfa/setup must be redeemable at POST /auth/mfa/verify");
        await AssertAccessTokenIssuedAsync(response);
    }

    [Fact]
    public async Task MfaVerify_ShouldReturn200_WhenRedeemingCodeMintedByAuthRecoveryCodesRegenerate()
    {
        // Mint path 2 of 4 — POST /auth/mfa/recovery-codes/regenerate, BCrypt cost-10.
        await ResetUserAsync();

        var codes = await MintViaAuthRegenerateAsync();

        var response = await RedeemAsync(codes[0]);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            because: "a recovery code returned by POST /auth/mfa/recovery-codes/regenerate must be redeemable");
        await AssertAccessTokenIssuedAsync(response);
    }

    [Fact]
    public async Task MfaVerify_ShouldReturn200_WhenRedeemingCodeMintedByProfileEnrollVerify()
    {
        // Mint path 3 of 4 — POST /profile/security/mfa/enroll/verify, salted SHA-256.
        // ⚠ THIS IS THE PATH THAT WAS BROKEN IN PRODUCTION: redemption returned
        // 500 {"detail":"Invalid salt version"} because BCrypt.Verify was run over a
        // 64-char hex digest. It is the end-to-end counterpart of the unit-level pin
        // MfaServiceTests.ValidateRecoveryCode_ShouldReturnTrue_WhenCodeMatchesSha256Digest.
        await ResetUserAsync();

        var codes = await MintViaProfileEnrollVerifyAsync();

        var response = await RedeemAsync(codes[0]);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            because: "wizard-minted codes are salted SHA-256 and must verify through IRecoveryCodeService, not BCrypt");
        await AssertAccessTokenIssuedAsync(response);
    }

    [Fact]
    public async Task MfaVerify_ShouldReturn200_WhenRedeemingCodeMintedByProfileRecoveryCodesRegenerate()
    {
        // Mint path 4 of 4 — POST /profile/security/recovery-codes/regenerate, salted SHA-256.
        // Also covers the spec scenario "Regenerating through the wizard does not
        // break redemption": the array starts life as BCrypt (mint path 1) and is
        // replaced wholesale with the SHA-256 family before redemption.
        await ResetUserAsync();
        await MintViaAuthMfaSetupAsync();

        var codes = await MintViaProfileRegenerateAsync();

        var response = await RedeemAsync(codes[0]);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            because: "codes regenerated through the wizard must redeem even though the array previously held BCrypt digests");
        await AssertAccessTokenIssuedAsync(response);
    }

    // ─── 4.3 — one-time use, both families ──────────────────────────────────

    [Fact]
    public async Task MfaVerify_ShouldRejectReplay_WhenSameRecoveryCodeUsedTwice()
    {
        // BCrypt family.
        await ResetUserAsync();
        var bcryptCodes = await MintViaAuthMfaSetupAsync();

        var firstBcrypt = await RedeemAsync(bcryptCodes[0]);
        var replayBcrypt = await RedeemAsync(bcryptCodes[0]);

        firstBcrypt.StatusCode.Should().Be(HttpStatusCode.OK);
        replayBcrypt.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            because: "redemption is one-time: the matched element is removed from the stored array on success");

        var afterBcrypt = await GetSeededUserAsync();
        afterBcrypt.MfaRecoveryCodes.Should().HaveCount(bcryptCodes.Length - 1,
            because: "exactly one element — the one that matched — is consumed");

        // Salted SHA-256 family.
        await ResetUserAsync();
        var sha256Codes = await MintViaProfileEnrollVerifyAsync();

        var firstSha256 = await RedeemAsync(sha256Codes[0]);
        var replaySha256 = await RedeemAsync(sha256Codes[0]);

        firstSha256.StatusCode.Should().Be(HttpStatusCode.OK);
        replaySha256.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            because: "one-time use must hold identically for the salted-SHA-256 family");

        var afterSha256 = await GetSeededUserAsync();
        afterSha256.MfaRecoveryCodes.Should().HaveCount(sha256Codes.Length - 1);
    }

    [Fact]
    public async Task MfaVerify_ShouldNotConsumeAnyElement_WhenSubmittedCodeMatchesNothing()
    {
        await ResetUserAsync();
        var codes = await MintViaProfileEnrollVerifyAsync();

        var response = await RedeemAsync("NOTMINTD");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            because: "a code matching no stored element is a clean 401 — not 200, and not 500");

        var user = await GetSeededUserAsync();
        user.MfaRecoveryCodes.Should().HaveCount(codes.Length,
            because: "a failed verification must leave the stored array untouched");
    }

    // ─── 4.4 — corrupt stored material yields 401, never 500 ────────────────

    [Fact]
    public async Task MfaVerify_ShouldReturn401NotServerError_WhenStoredDigestIsMalformed()
    {
        // "code1"/"code2" are the exact placeholder values the endpoint-test fixtures
        // seed. They are neither digest family — the shape a manual database edit or
        // a partially-applied migration leaves behind. Pre-fix, BCrypt.Verify over one
        // of them threw BCrypt.Net.SaltParseException, which derives directly from
        // Exception and so fell through ErrorHandlingMiddleware's `_` arm to HTTP 500
        // with ProblemDetails.Detail carrying the raw "Invalid salt version".
        await ResetUserAsync();
        var user = await GetSeededUserAsync();
        user.MfaEnabled = true;
        user.MfaRecoveryCodes = ["code1", "code2"];

        var response = await RedeemAsync("code1");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            because: "corrupt stored material must fail verification, not crash the request — a 500 here is a "
                   + "denial of service on the account-recovery path and leaks the storage format");
        ((int)response.StatusCode).Should().BeLessThan(500,
            because: "no content of users.mfa_recovery_codes may produce a server error at POST /auth/mfa/verify");

        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("Invalid salt version",
            because: "the BCrypt library's parse message must never reach the caller");
        body.Should().NotContainEquivalentOf("bcrypt",
            because: "the response must not disclose which cryptography library backs the stored digests");
        body.Should().NotContainEquivalentOf("salt",
            because: "no hash-shape detail may leak through the failure response (ADR-0013 rationale)");
    }

    // ─── Mint helpers — always through the real endpoint ────────────────────

    /// <summary>Mint path 1 — <c>POST /auth/mfa/setup</c> (BCrypt cost-10).</summary>
    private async Task<string[]> MintViaAuthMfaSetupAsync()
    {
        var response = await _authClient.PostAsync("/api/v1/auth/mfa/setup", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            because: "the mint half must succeed before the redemption half can be meaningful");

        return await ReadRecoveryCodesAsync(response);
    }

    /// <summary>Mint path 2 — <c>POST /auth/mfa/recovery-codes/regenerate</c> (BCrypt cost-10).</summary>
    private async Task<string[]> MintViaAuthRegenerateAsync()
    {
        // This endpoint gates on MfaEnabled + a password challenge; the seeded
        // fixture user is API-key authenticated and carries neither by default.
        var user = await GetSeededUserAsync();
        user.MfaEnabled = true;
        user.PasswordHash = PasswordService.HashPassword(TestPassword);

        var response = await _authClient.PostAsJsonAsync(
            "/api/v1/auth/mfa/recovery-codes/regenerate",
            new { password = TestPassword });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return await ReadRecoveryCodesAsync(response);
    }

    /// <summary>Mint path 3 — <c>POST /profile/security/mfa/enroll/verify</c> (salted SHA-256).</summary>
    private async Task<string[]> MintViaProfileEnrollVerifyAsync()
    {
        // The wizard mints only after a live TOTP validates against the secret
        // returned by /init — same technique MfaEnrollEndpointsTests uses.
        var initResponse = await _authClient.PostAsync("/api/v1/profile/security/mfa/enroll/init", null);
        initResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var initDoc = JsonDocument.Parse(await initResponse.Content.ReadAsStringAsync());
        var secret = initDoc.RootElement.GetProperty("secret").GetString()!;

        var response = await _authClient.PostAsJsonAsync(
            "/api/v1/profile/security/mfa/enroll/verify",
            new { secret, totpCode = ComputeTotp(secret) });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return await ReadRecoveryCodesAsync(response);
    }

    /// <summary>Mint path 4 — <c>POST /profile/security/recovery-codes/regenerate</c> (salted SHA-256).</summary>
    private async Task<string[]> MintViaProfileRegenerateAsync()
    {
        // Requires an already-enrolled user plus a fresh TOTP step-up.
        var secretBytes = KeyGeneration.GenerateRandomKey(20);
        var secret = Base32Encoding.ToString(secretBytes);

        var user = await GetSeededUserAsync();
        user.MfaEnabled = true;
        user.MfaSecret = secret;

        var response = await _authClient.PostAsJsonAsync(
            "/api/v1/profile/security/recovery-codes/regenerate",
            new { totpCode = ComputeTotp(secret) });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return await ReadRecoveryCodesAsync(response);
    }

    // ─── Redemption helper — always through the real endpoint ───────────────

    /// <summary>
    /// Drives a full challenge→redeem round trip: stores a pending MFA entry for
    /// the seeded user, then posts the plaintext code to the real anonymous
    /// <c>POST /api/v1/auth/mfa/verify</c>.
    /// </summary>
    private async Task<HttpResponseMessage> RedeemAsync(string recoveryCode)
    {
        var mfaToken = await AuthEndpoints.GenerateMfaChallengeTokenAndStoreAsync(
            AuthenticatedPlatformApiFactory.TestUserId,
            AuthenticatedPlatformApiFactory.TestTenantId,
            _factory.Services.GetRequiredService<IMfaPendingCache>(),
            CancellationToken.None);

        return await _anonClient.PostAsJsonAsync(
            "/api/v1/auth/mfa/verify",
            new { mfaToken, recoveryCode });
    }

    // ─── Fixture helpers ────────────────────────────────────────────────────

    private static async Task AssertAccessTokenIssuedAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.TryGetProperty("accessToken", out var token).Should().BeTrue(
            because: "a successful redemption completes the login and must return an access token");
        token.GetString().Should().NotBeNullOrWhiteSpace();
    }

    private static async Task<string[]> ReadRecoveryCodesAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var codes = doc.RootElement.GetProperty("recoveryCodes").EnumerateArray()
            .Select(e => e.GetString()!)
            .ToArray();

        codes.Should().NotBeEmpty(because: "the mint endpoint returns the plaintext codes exactly once");
        return codes;
    }

    private static string ComputeTotp(string secret) =>
        new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp(DateTime.UtcNow);

    private async Task<User> GetSeededUserAsync()
    {
        var user = await _factory.Services.GetRequiredService<IUserStore>().GetByIdAsync(
            new TenantId(AuthenticatedPlatformApiFactory.TestTenantId),
            EntityId.From(AuthenticatedPlatformApiFactory.TestUserId),
            CancellationToken.None);

        user.Should().NotBeNull(because: "the factory seeds exactly one admin user for the test tenant");
        return user!;
    }

    /// <summary>
    /// Restores the seeded user to a known pre-MFA baseline. The factory's
    /// <c>IUserStore</c> substitute hands every request the SAME mutable instance,
    /// so without this reset a mint from an earlier test would satisfy (or block)
    /// the next one's preconditions.
    /// </summary>
    private async Task ResetUserAsync()
    {
        var user = await GetSeededUserAsync();
        user.MfaEnabled = false;
        user.MfaSecret = null;
        user.MfaRecoveryCodes = null;
        user.MfaConfirmedAt = null;
        user.PasswordHash = null;
        user.FailedLoginAttempts = 0;
        user.LockedUntil = null;
    }
}
