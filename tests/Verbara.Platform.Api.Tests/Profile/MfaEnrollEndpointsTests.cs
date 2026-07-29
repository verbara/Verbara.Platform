using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Verbara.Platform.Api.Tests.Profile;

/// <summary>
/// Integration tests for the profile-scoped MFA wizard, sessions, and
/// recovery-code regeneration endpoints (R5.2 PA.2 / S3.4 Frente E).
/// </summary>
/// <remarks>
/// The shared <see cref="AuthenticatedPlatformApiFactory"/> seeds a test admin
/// user with no MFA configured, so the enroll/init + verify path exercises the
/// happy path without re-seeding storage. Sessions tests rely on the in-memory
/// <c>IRefreshTokenStore</c> behaviour: with no real login flow having
/// occurred, the user has zero sessions, so list returns an empty array and
/// revoke against a missing id returns 404. These contracts are exactly what
/// the wizard's empty/error UX must render.
/// </remarks>
public sealed class MfaEnrollEndpointsTests : IClassFixture<AuthenticatedPlatformApiFactory>
{
    private readonly HttpClient _authClient;
    private readonly HttpClient _anonClient;

    public MfaEnrollEndpointsTests(AuthenticatedPlatformApiFactory factory)
    {
        _authClient = factory.CreateAuthenticatedClient();
        _anonClient = factory.CreateClient();
    }

    // ─── /profile/security/mfa/enroll/init ──────────────────────────────────

    [Fact]
    public async Task Init_ShouldReturn401_WhenUnauthenticated()
    {
        var response = await _anonClient.PostAsync("/api/v1/profile/security/mfa/enroll/init", null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Init_ShouldReturnSecretAndQrCode_WhenAuthenticated()
    {
        var response = await _authClient.PostAsync("/api/v1/profile/security/mfa/enroll/init", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        root.GetProperty("secret").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("qrUri").GetString().Should().StartWith("otpauth://totp/");
        root.GetProperty("manualCode").GetString().Should().NotBeNullOrWhiteSpace();
    }

    // ─── /profile/security/mfa/enroll/verify ────────────────────────────────

    [Fact]
    public async Task Verify_ShouldReturn400_WhenSecretIsMissing()
    {
        var response = await _authClient.PostAsJsonAsync(
            "/api/v1/profile/security/mfa/enroll/verify",
            new { totpCode = "123456" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Verify_ShouldReturn400_WhenTotpCodeInvalid()
    {
        // Init first to obtain a fresh secret.
        var initRes = await _authClient.PostAsync("/api/v1/profile/security/mfa/enroll/init", null);
        var initBody = await initRes.Content.ReadAsStringAsync();
        using var initDoc = JsonDocument.Parse(initBody);
        var secret = initDoc.RootElement.GetProperty("secret").GetString()!;

        var response = await _authClient.PostAsJsonAsync(
            "/api/v1/profile/security/mfa/enroll/verify",
            new { secret, totpCode = "000000" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Verify_ShouldReturnRecoveryCodes_WhenTotpValid()
    {
        var initRes = await _authClient.PostAsync("/api/v1/profile/security/mfa/enroll/init", null);
        var initBody = await initRes.Content.ReadAsStringAsync();
        using var initDoc = JsonDocument.Parse(initBody);
        var secret = initDoc.RootElement.GetProperty("secret").GetString()!;

        // Generate the live TOTP for this secret using the same OtpNet impl the
        // production code uses, so the assertion is not clock-skew flaky.
        //
        // SCOPE — read this before trusting the test for anything else. What it
        // covers: TOTP verification against the /init secret, and the SHAPE of the
        // recovery codes handed back (10 codes, 8 chars each). What it does NOT
        // cover: recovery-code REDEMPTION. The plaintext codes are asserted on and
        // then discarded — this test never posts one to POST /auth/mfa/verify, so
        // it says nothing about whether the digests persisted here can be redeemed.
        //
        // An earlier version of this comment claimed the test "proves the
        // verify→persist branch works end-to-end". It meant the TOTP branch, but it
        // read as redemption coverage, and that reading is exactly why the wizard
        // shipped minting salted-SHA-256 digests that POST /auth/mfa/verify could
        // not verify — redemption returned 500 "Invalid salt version" in production
        // while this suite stayed green. The mint→redeem seam is covered by
        // Mfa/MfaRecoveryCodeRedemptionTests; do not treat this test as a substitute.
        var live = OtpNet.Base32Encoding.ToBytes(secret);
        var totp = new OtpNet.Totp(live);
        var code = totp.ComputeTotp(DateTime.UtcNow);

        var response = await _authClient.PostAsJsonAsync(
            "/api/v1/profile/security/mfa/enroll/verify",
            new { secret, totpCode = code });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var codes = doc.RootElement.GetProperty("recoveryCodes").EnumerateArray()
            .Select(e => e.GetString()!)
            .ToArray();
        codes.Should().HaveCount(10);
        codes.Should().AllSatisfy(c => c.Length.Should().Be(8));
    }

    // ─── /profile/security/mfa/enroll/complete ──────────────────────────────

    [Fact]
    public async Task Complete_ShouldReturn400_WhenAcknowledgedFalse()
    {
        var response = await _authClient.PostAsJsonAsync(
            "/api/v1/profile/security/mfa/enroll/complete",
            new { acknowledged = false });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ─── /profile/security/sessions ─────────────────────────────────────────

    [Fact]
    public async Task Sessions_ShouldReturn401_WhenUnauthenticated()
    {
        var response = await _anonClient.GetAsync("/api/v1/profile/security/sessions/");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Sessions_ShouldListUserActiveSessions_WhenAuthenticated()
    {
        var response = await _authClient.GetAsync("/api/v1/profile/security/sessions/");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        // The shared factory authenticates via API key so no real refresh
        // token has been issued; the result is a (possibly empty) array.
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task RevokeSession_ShouldReturn404_WhenSessionMissing()
    {
        var response = await _authClient.PostAsync(
            "/api/v1/profile/security/sessions/non-existent-session-id/revoke",
            null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ─── /profile/security/recovery-codes/regenerate ─────────────────────────

    [Fact]
    public async Task RegenerateRecoveryCodes_ShouldReturn400_WhenMfaNotEnabled()
    {
        // Test user has no MFA configured by default.
        var response = await _authClient.PostAsJsonAsync(
            "/api/v1/profile/security/recovery-codes/regenerate",
            new { totpCode = "123456" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
