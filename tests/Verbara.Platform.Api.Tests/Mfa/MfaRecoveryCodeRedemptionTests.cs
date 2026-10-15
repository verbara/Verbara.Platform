using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Verbara.Platform.Api.Endpoints;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Api.Tests.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Identity.Mfa;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
/// Since <c>harden-recovery-code-digest</c> every mint path stores Argon2id, so the endpoint-minted
/// tests exercise that family, each through its own plaintext generator (16 hex characters on
/// <c>/auth/mfa/*</c>, 8 Base32 characters on the profile paths). The two legacy families (BCrypt
/// cost 10 and salted SHA-256) can no longer be minted; the tests that pin them seed digests forged the
/// way the old mint paths wrote them, and are named for it.
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
/// <para>
/// The suite mints for that one user many times a second, so it runs on
/// <see cref="UnthrottledMintApiFactory"/>, which turns the per-user mint gate off
/// (<c>Mfa:RecoveryCodeMint:MinInterval</c> = 0). The gate has its own suites:
/// <c>RecoveryCodeMintThrottleTests</c> and <c>RecoveryCodeMintGateTests</c>.
/// </para>
/// </remarks>
public sealed class MfaRecoveryCodeRedemptionTests : IClassFixture<MfaRecoveryCodeRedemptionTests.UnthrottledMintApiFactory>
{
    /// <summary>The authenticated factory with the per-user recovery-code mint gate turned off.</summary>
    public sealed class UnthrottledMintApiFactory : AuthenticatedPlatformApiFactory
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mfa:RecoveryCodeMint:MinInterval"] = "00:00:00",
            }));
            return base.CreateHost(builder);
        }
    }

    private const string TestPassword = "CurrentPassword123!";

    private readonly UnthrottledMintApiFactory _factory;
    private readonly HttpClient _authClient;
    private readonly HttpClient _anonClient;

    public MfaRecoveryCodeRedemptionTests(UnthrottledMintApiFactory factory)
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
        // Mint path 1 of 4 — POST /auth/mfa/setup (Argon2id; BCrypt cost-10 before harden-recovery-code-digest).
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
        // Mint path 2 of 4 — POST /auth/mfa/recovery-codes/regenerate (Argon2id; BCrypt cost-10 before).
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
        // Mint path 3 of 4 — POST /profile/security/mfa/enroll/verify (Argon2id; salted SHA-256 before).
        // ⚠ THIS IS THE PATH THAT WAS BROKEN IN PRODUCTION: redemption returned
        // 500 {"detail":"Invalid salt version"} because BCrypt.Verify was run over the
        // 64-char hex digest this path then wrote. The legacy SHA-256 half of that pin is now
        // MfaVerify_ShouldReturn200_WhenRedeemingPreChangeSha256Code and the unit-level
        // MfaServiceTests.ValidateRecoveryCode_ShouldReturnTrue_WhenCodeMatchesSha256Digest.
        await ResetUserAsync();

        var codes = await MintViaProfileEnrollVerifyAsync();

        var response = await RedeemAsync(codes[0]);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            because: "a recovery code returned by POST /profile/security/mfa/enroll/verify must be redeemable");
        await AssertAccessTokenIssuedAsync(response);
    }

    [Fact]
    public async Task MfaVerify_ShouldReturn200_WhenRedeemingCodeMintedByProfileRecoveryCodesRegenerate()
    {
        // Mint path 4 of 4 — POST /profile/security/recovery-codes/regenerate (Argon2id; salted SHA-256 before).
        // Also covers the spec scenario "Regenerating through the wizard does not
        // break redemption": the array starts life minted by path 1 and is replaced
        // wholesale before redemption.
        await ResetUserAsync();
        await MintViaAuthMfaSetupAsync();

        var codes = await MintViaProfileRegenerateAsync();

        var response = await RedeemAsync(codes[0]);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            because: "codes regenerated through the wizard must redeem after replacing an earlier array");
        await AssertAccessTokenIssuedAsync(response);
    }

    // ─── 4.3 — one-time use, both plaintext generators ──────────────────────

    [Fact]
    public async Task MfaVerify_ShouldRejectReplay_WhenSameRecoveryCodeUsedTwice()
    {
        // 16-hex codes from /auth/mfa/setup (BCrypt before harden-recovery-code-digest, Argon2id now).
        await ResetUserAsync();
        var hexCodes = await MintViaAuthMfaSetupAsync();

        var firstHex = await RedeemAsync(hexCodes[0]);
        var replayHex = await RedeemAsync(hexCodes[0]);

        firstHex.StatusCode.Should().Be(HttpStatusCode.OK);
        replayHex.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            because: "redemption is one-time: the matched element is removed from the stored array on success");

        var afterHex = await GetSeededUserAsync();
        afterHex.MfaRecoveryCodes.Should().HaveCount(hexCodes.Length - 1,
            because: "exactly one element — the one that matched — is consumed");

        // 8-char codes from the profile wizard (salted SHA-256 before, Argon2id now).
        await ResetUserAsync();
        var wizardCodes = await MintViaProfileEnrollVerifyAsync();

        var firstWizard = await RedeemAsync(wizardCodes[0]);
        var replayWizard = await RedeemAsync(wizardCodes[0]);

        firstWizard.StatusCode.Should().Be(HttpStatusCode.OK);
        replayWizard.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            because: "one-time use must hold identically for codes from the profile generator");

        var afterWizard = await GetSeededUserAsync();
        afterWizard.MfaRecoveryCodes.Should().HaveCount(wizardCodes.Length - 1);
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

    // ─── harden-recovery-code-digest 1.1 — every mint path writes Argon2id ──

    [Fact]
    public async Task MintPath_ShouldStoreOnlyArgon2idDigests_WhenCodesAreMintedByAuthMfaSetup()
    {
        await ResetUserAsync();

        var codes = await MintViaAuthMfaSetupAsync();

        await AssertStoredArgon2idAndRedeemableAsync(codes);
    }

    [Fact]
    public async Task MintPath_ShouldStoreOnlyArgon2idDigests_WhenCodesAreMintedByAuthRecoveryCodesRegenerate()
    {
        await ResetUserAsync();

        var codes = await MintViaAuthRegenerateAsync();

        await AssertStoredArgon2idAndRedeemableAsync(codes);
    }

    [Fact]
    public async Task MintPath_ShouldStoreOnlyArgon2idDigests_WhenCodesAreMintedByProfileEnrollVerify()
    {
        await ResetUserAsync();

        var codes = await MintViaProfileEnrollVerifyAsync();

        await AssertStoredArgon2idAndRedeemableAsync(codes);
    }

    [Fact]
    public async Task MintPath_ShouldStoreOnlyArgon2idDigests_WhenCodesAreMintedByProfileRecoveryCodesRegenerate()
    {
        await ResetUserAsync();

        var codes = await MintViaProfileRegenerateAsync();

        await AssertStoredArgon2idAndRedeemableAsync(codes);
    }

    // ─── harden-recovery-code-digest 1.4 — the endpoint seam with Argon2id ──

    [Fact]
    public async Task MfaVerify_ShouldReturn401AndConsumeNothing_WhenArgon2idArrayGetsWrongCode()
    {
        await ResetUserAsync();
        var stored = await SeedRecoveryCodesAsync(ForgeArgon2idArray());

        var response = await RedeemAsync("ZZZZ9999");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            because: "a code matching no Argon2id element is a clean 401");

        var user = await GetSeededUserAsync();
        user.MfaRecoveryCodes.Should().Equal(stored,
            because: "a failed verification must leave every stored element in place");
    }

    [Fact]
    public async Task MfaVerify_ShouldReturn401NotServerError_WhenStoredArgon2idDigestHasZeroTimeCost()
    {
        // Isopoh 2.0.0 throws ArgumentException("TimeCost must be 1 or more, got 0") for this
        // digest. The plaintext is the one the digest was derived from, so an unguarded
        // Argon2id branch would reach the throw.
        await ResetUserAsync();
        await SeedRecoveryCodesAsync([Argon2idTestDigests.Corrupt("t=0")]);

        var response = await RedeemAsync(Argon2idTestDigests.GoodPlaintext);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            because: "corrupt stored material must fail verification, never produce a 400 or 500");

        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContainEquivalentOf("TimeCost",
            because: "the Argon2 library's message must never reach the caller");
        body.Should().NotContainEquivalentOf("argon",
            because: "the response must not disclose which cryptography library backs the stored digests");
    }

    [Fact]
    public async Task MfaVerify_ShouldRejectReplay_WhenSameArgon2idCodeUsedTwice()
    {
        await ResetUserAsync();
        var stored = await SeedRecoveryCodesAsync(ForgeArgon2idArray());

        var first = await RedeemAsync(s_argon2idPlaintexts[1]);
        var replay = await RedeemAsync(s_argon2idPlaintexts[1]);

        first.StatusCode.Should().Be(HttpStatusCode.OK,
            because: "an Argon2id-stored code must redeem once");
        replay.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            because: "redemption is one-time for the Argon2id family as for the legacy ones");

        var user = await GetSeededUserAsync();
        user.MfaRecoveryCodes.Should().Equal(new[] { stored[0], stored[2] },
            because: "exactly the matched element is consumed and the other two remain");
    }

    // ─── harden-recovery-code-digest 1.5 — legacy keeps working, no regenerate

    [Fact]
    public async Task MfaVerify_ShouldReturn200_WhenRedeemingPreChangeBcryptCode()
    {
        // Forged exactly as POST /auth/mfa/setup wrote it before this change.
        const string code = "0123456789abcdef";
        await ResetUserAsync();
        await SeedRecoveryCodesAsync([BCrypt.Net.BCrypt.HashPassword(code, workFactor: 10)]);

        var response = await RedeemAsync(code);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            because: "a BCrypt code minted before the deploy must keep redeeming with no regenerate");
        await AssertAccessTokenIssuedAsync(response);
    }

    [Fact]
    public async Task MfaVerify_ShouldReturn200_WhenRedeemingPreChangeSha256Code()
    {
        // Forged exactly as the profile wizard wrote it before this change: salted with the user id.
        const string code = "WIZARDCD";
        await ResetUserAsync();
        await SeedRecoveryCodesAsync([new RecoveryCodeService().Hash(code, AuthenticatedPlatformApiFactory.TestUserId)]);

        var response = await RedeemAsync(code);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            because: "a salted-SHA-256 code minted before the deploy must keep redeeming with no regenerate");
        await AssertAccessTokenIssuedAsync(response);
    }

    // ─── harden-recovery-code-digest 2.5 — the redemption counter ───────────

    [Fact]
    public async Task MfaVerify_ShouldCountRedemptionByFamily_WhenRecoveryCodeIsConsumed()
    {
        // mfa.recovery_codes.redeemed{family} counts consumed codes, never attempts. The listener is scoped
        // to this factory's IMeterFactory, so redemptions in other test hosts cannot leak in.
        const string bcryptCode = "0123456789abcdef";
        var meterFactory = _factory.Services.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>();
        var redeemed = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == RecoveryCodeFamilyMetrics.MeterName
                && ReferenceEquals(instrument.Meter.Scope, meterFactory)
                && instrument.Name == "mfa.recovery_codes.redeemed")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "family")
                {
                    for (var i = 0; i < value; i++)
                        redeemed.Enqueue((string)tag.Value!);
                }
            }
        });
        listener.Start();

        await ResetUserAsync();
        var stored = await SeedRecoveryCodesAsync([
            Argon2idTestDigests.Good,
            BCrypt.Net.BCrypt.HashPassword(bcryptCode, workFactor: 10),
        ]);

        (await RedeemAsync("ZZZZ9999")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        redeemed.Should().BeEmpty(because: "a rejected code consumes nothing and is not a redemption");

        (await RedeemAsync(bcryptCode)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await RedeemAsync(Argon2idTestDigests.GoodPlaintext)).StatusCode.Should().Be(HttpStatusCode.OK);

        redeemed.Should().Equal(["bcrypt", "argon2id"],
            because: "each consumed code is counted once, under the family it was stored in");
        (await GetSeededUserAsync()).MfaRecoveryCodes.Should().BeEmpty(
            because: "both stored elements (of {0}) were consumed", stored.Length);
    }

    // ─── Mint helpers — always through the real endpoint ────────────────────

    /// <summary>Mint path 1 — <c>POST /auth/mfa/setup</c> (16 hex characters, Argon2id).</summary>
    private async Task<string[]> MintViaAuthMfaSetupAsync()
    {
        var response = await _authClient.PostAsync("/api/v1/auth/mfa/setup", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            because: "the mint half must succeed before the redemption half can be meaningful");

        return await ReadRecoveryCodesAsync(response);
    }

    /// <summary>Mint path 2 — <c>POST /auth/mfa/recovery-codes/regenerate</c> (16 hex characters, Argon2id).</summary>
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

    /// <summary>Mint path 3 — <c>POST /profile/security/mfa/enroll/verify</c> (8 Base32 characters, Argon2id).</summary>
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

    /// <summary>Mint path 4 — <c>POST /profile/security/recovery-codes/regenerate</c> (8 Base32 characters, Argon2id).</summary>
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

    /// <summary>Three normalised plaintexts for the forged Argon2id arrays (1.4).</summary>
    private static readonly string[] s_argon2idPlaintexts = ["ABCD2345", "EFGH6789", "JKMN2345"];

    /// <summary>
    /// Forges an Argon2id array at the production parameters, one element per
    /// <see cref="s_argon2idPlaintexts"/> entry. Used only where no mint path writes Argon2id
    /// yet (1.4); the mint-path tests (1.1) never forge.
    /// </summary>
    private static string[] ForgeArgon2idArray() =>
        s_argon2idPlaintexts.Select(PasswordService.HashPassword).ToArray();

    /// <summary>Puts the seeded user into the enrolled state with the given stored digests.</summary>
    private async Task<string[]> SeedRecoveryCodesAsync(string[] stored)
    {
        var user = await GetSeededUserAsync();
        user.MfaEnabled = true;
        user.MfaRecoveryCodes = [.. stored];
        return stored;
    }

    /// <summary>
    /// 1.1's two-part check: every element the mint stored is an Argon2id digest at the
    /// production parameters, and a returned plaintext then redeems at the real endpoint.
    /// </summary>
    private async Task AssertStoredArgon2idAndRedeemableAsync(string[] codes)
    {
        var user = await GetSeededUserAsync();
        user.MfaRecoveryCodes.Should().NotBeNullOrEmpty();
        user.MfaRecoveryCodes.Should().AllSatisfy(d => d.Should().StartWith(Argon2idTestDigests.ProductionPrefix,
            because: "every mint path must persist Argon2id at the password parameters (design D1, D4)"));

        var response = await RedeemAsync(codes[0]);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            because: "a code the mint returned must redeem at POST /auth/mfa/verify");
        await AssertAccessTokenIssuedAsync(response);
    }


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
