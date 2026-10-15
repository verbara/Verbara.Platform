using Verbara.Platform.Api.Services;
using Verbara.Platform.Identity.Mfa;
using OtpNet;

namespace Verbara.Platform.Api.Tests.Services;

public sealed class MfaServiceTests
{
    /// <summary>
    /// Stand-in for <c>user.UserId.Value</c> — the salt
    /// <see cref="IRecoveryCodeService.Hash"/> mixes into every salted-SHA-256
    /// digest, and therefore the salt <c>AuthEndpoints.MfaVerify</c> must hand
    /// back to <see cref="MfaService.ValidateRecoveryCode"/> at redemption time.
    /// </summary>
    private const string TestSalt = "user-recovery-salt-001";

    /// <summary>
    /// The real implementation, never a substitute: the salted-SHA-256 branch of
    /// <see cref="MfaService.ValidateRecoveryCode"/> is only meaningful against
    /// the same digest the wizard mint paths actually persist.
    /// </summary>
    private static readonly RecoveryCodeService s_recoveryCodes = new();

    [Fact]
    public void GenerateSetup_ShouldReturnSecretAndQrUri()
    {
        var (secret, qrUri) = MfaService.GenerateSetup("user@test.com");

        secret.Should().NotBeNullOrWhiteSpace();
        qrUri.Should().Contain("otpauth://totp/");
        qrUri.Should().Contain("user%40test.com");
        qrUri.Should().Contain($"secret={secret}");
    }

    [Fact]
    public void VerifyCode_ShouldReturnTrue_WhenCodeIsValid()
    {
        var (secret, _) = MfaService.GenerateSetup("user@test.com");

        // Generate the current valid code using the same secret
        var secretBytes = Base32Encoding.ToBytes(secret);
        var totp = new Totp(secretBytes);
        var code = totp.ComputeTotp();

        MfaService.VerifyCode(secret, code).Should().BeTrue();
    }

    [Fact]
    public void VerifyCode_ShouldReturnFalse_WhenCodeIsInvalid()
    {
        var (secret, _) = MfaService.GenerateSetup("user@test.com");

        MfaService.VerifyCode(secret, "000000").Should().BeFalse();
    }

    [Fact]
    public void GenerateRecoveryCodes_ShouldReturn10UniqueCodes()
    {
        var codes = MfaService.GenerateRecoveryCodes();

        codes.Should().HaveCount(10);
        codes.Distinct().Should().HaveCount(10);
        codes.Should().AllSatisfy(c => c.Length.Should().Be(16)); // 8 bytes = 16 hex chars
    }

    [Fact]
    public void ValidateRecoveryCode_ShouldReturnTrue_WhenCodeMatches()
    {
        var codes = MfaService.GenerateRecoveryCodes();
        var hashed = MfaService.HashRecoveryCodes(codes);

        var (isValid, index) = MfaService.ValidateRecoveryCode(
            s_recoveryCodes, codes[3], hashed, TestSalt);

        isValid.Should().BeTrue();
        index.Should().Be(3);
    }

    [Fact]
    public void ValidateRecoveryCode_ShouldReturnFalse_WhenCodeDoesNotMatch()
    {
        var codes = MfaService.GenerateRecoveryCodes();
        var hashed = MfaService.HashRecoveryCodes(codes);

        var (isValid, index) = MfaService.ValidateRecoveryCode(
            s_recoveryCodes, "not-a-valid-code", hashed, TestSalt);

        isValid.Should().BeFalse();
        index.Should().Be(-1);
    }

    // ─── Per-element digest dispatch (fix-recovery-code-redemption) ─────────
    //
    // Three digest families coexist in users.mfa_recovery_codes:
    //   • Argon2id              — minted by all four paths via MfaService.HashRecoveryCodes
    //                             since harden-recovery-code-digest.
    //   • BCrypt cost-10        — legacy: what POST /auth/mfa/setup and
    //                             POST /auth/mfa/recovery-codes/regenerate wrote before,
    //                             forged here with BCrypt.HashPassword(code, 10).
    //   • salted SHA-256 (hex)  — legacy: what the profile wizard wrote before, via
    //                             IRecoveryCodeService.Hash(code, userId).
    // Every test below verifies against a digest produced by the code that minted (or
    // used to mint) that family, never by re-hashing by hand inside the assertion.

    [Fact]
    public void ValidateRecoveryCode_ShouldReturnTrue_WhenCodeMatchesSha256Digest()
    {
        // ⚠ REGRESSION PIN — THIS TEST FAILS ON origin/main. It is the direct
        // pin for the defect this change closes: the old implementation ran
        // BCrypt.Verify over EVERY stored element, so a 64-char salted-SHA-256
        // digest minted by the profile enrollment wizard raised
        // BCrypt.Net.SaltParseException("Invalid salt version"), which escaped
        // to ErrorHandlingMiddleware as HTTP 500 — the wizard's recovery codes
        // were simply unredeemable. If per-element dispatch is ever removed,
        // this assertion goes red instead of the suite staying green.
        var plaintext = s_recoveryCodes.Generate();
        var stored = plaintext.Select(c => s_recoveryCodes.Hash(c, TestSalt)).ToList();

        var (isValid, index) = MfaService.ValidateRecoveryCode(
            s_recoveryCodes, plaintext[2], stored, TestSalt);

        isValid.Should().BeTrue(
            because: "a salted-SHA-256 digest must be verified through IRecoveryCodeService, not BCrypt");
        index.Should().Be(2, because: "the caller removes exactly the element that matched");
    }

    [Fact]
    public void ValidateRecoveryCode_ShouldReturnTrue_WhenCodeMatchesBcryptDigest()
    {
        // A legacy family — unchanged behaviour, pinned so neither the SHA-256 nor the
        // Argon2id branch can be introduced at the cost of this one. The digests are what
        // the /auth/mfa/* paths wrote before harden-recovery-code-digest (BCrypt cost 10).
        var plaintext = MfaService.GenerateRecoveryCodes().Take(2).ToList();
        var stored = plaintext.Select(c => BCrypt.Net.BCrypt.HashPassword(c, workFactor: 10)).ToList();

        var (isValid, index) = MfaService.ValidateRecoveryCode(
            s_recoveryCodes, plaintext[0], stored, TestSalt);

        isValid.Should().BeTrue(
            because: "codes minted by POST /auth/mfa/setup must keep redeeming across the deploy");
        index.Should().Be(0);
    }

    [Fact]
    public void ValidateRecoveryCode_ShouldMatchPerElement_WhenArrayMixesBothFamilies()
    {
        // A mixed array is not reachable through the API today (every mint path
        // replaces the array wholesale) but a partially-applied migration or a
        // manual database edit produces one. A whole-array decision would fail
        // such a row on its first element; dispatch must be per element.
        const string bcryptPlaintext = "legacy-code-aabbccdd";
        const string sha256Plaintext = "WIZARDCD";

        var stored = new List<string>
        {
            BCrypt.Net.BCrypt.HashPassword(bcryptPlaintext, workFactor: 10),
            s_recoveryCodes.Hash(sha256Plaintext, TestSalt),
        };

        var bcryptMatch = MfaService.ValidateRecoveryCode(
            s_recoveryCodes, bcryptPlaintext, stored, TestSalt);
        var sha256Match = MfaService.ValidateRecoveryCode(
            s_recoveryCodes, sha256Plaintext, stored, TestSalt);

        bcryptMatch.IsValid.Should().BeTrue(
            because: "element 0 is a BCrypt digest and must be verified with BCrypt");
        bcryptMatch.Index.Should().Be(0);

        sha256Match.IsValid.Should().BeTrue(
            because: "element 1 is a salted-SHA-256 digest and must be verified with IRecoveryCodeService, "
                   + "even though element 0 of the same array is BCrypt");
        sha256Match.Index.Should().Be(1);
    }

    [Fact]
    public void ValidateRecoveryCode_ShouldReturnFalse_WhenStoredDigestIsMalformed()
    {
        // "code1" is the exact placeholder the endpoint-test fixtures seed — neither
        // digest family, the shape a manual database edit leaves behind. It MUST be a
        // non-match and it MUST NOT raise: BCrypt.Net.SaltParseException derives
        // directly from Exception, so it falls through ErrorHandlingMiddleware's `_`
        // arm to HTTP 500 carrying the raw "Invalid salt version" library message.
        // Pre-fix, BCrypt.Verify("code1", "code1") threw exactly that.
        //
        // This case covers values that do NOT start with "$2". The nastier half —
        // values corrupt INSIDE the "$2" family, which raise IndexOutOfRangeException /
        // FormatException / ArgumentOutOfRangeException rather than SaltParseException —
        // is covered by ValidateRecoveryCode_ShouldReturnFalse_WhenStoredDigestIsCorruptInsideTheBcryptFamily.
        // Both funnel through BcryptVerifyGuard.SafeVerify, which is the single place
        // the "stored material never raises" requirement is enforced.
        var stored = new[] { "code1", "code2" };

        var act = () => MfaService.ValidateRecoveryCode(
            s_recoveryCodes, "code1", stored, TestSalt);

        act.Should().NotThrow(
            because: "verification is fail-closed: stored material must never raise out of this method");

        var (isValid, index) = act();
        isValid.Should().BeFalse(
            because: "a plaintext equal to a malformed stored value is still not a positive verification");
        index.Should().Be(-1);
    }

    [Theory]
    [InlineData("$2a$10$truncated")]
    [InlineData("$2a$xx$abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXY")]
    [InlineData("$2")]
    [InlineData("$2a$")]
    public void ValidateRecoveryCode_ShouldReturnFalse_WhenStoredDigestIsCorruptInsideTheBcryptFamily(string corrupt)
    {
        // The nastier half of "malformed": a value that DOES start with "$2", so the
        // prefix dispatch routes it to BCrypt, but that BCrypt.Net-Next cannot parse.
        // It raises ArgumentOutOfRangeException / FormatException / IndexOutOfRangeException
        // rather than SaltParseException, so a guard that catches only SaltParseException
        // lets it escape — FormatException and IndexOutOfRangeException land in
        // ErrorHandlingMiddleware's `_` arm as HTTP 500, which is precisely the outcome
        // this change exists to make impossible.
        //
        // Unreachable through the API today (no mint path writes such a value), but it is
        // exactly the "manual database edit / truncated column" case the spec names when it
        // says the endpoint MUST NOT return 500 for ANY content of mfa_recovery_codes.
        var stored = new[] { corrupt };

        var act = () => MfaService.ValidateRecoveryCode(
            s_recoveryCodes, "some-plaintext-code", stored, TestSalt);

        act.Should().NotThrow(
            because: "no content of the stored column may raise out of the verifier — a corrupt "
                   + "digest is a failed verification, never a 500");

        var (isValid, index) = act();
        isValid.Should().BeFalse();
        index.Should().Be(-1);
    }

    [Fact]
    public void ValidateRecoveryCode_ShouldReturnFalse_WhenCodeMatchesNothing()
    {
        // Per family: a wrong code must be rejected cleanly in each, with no
        // element consumed (index -1 means the caller removes nothing).
        var bcryptPlaintext = MfaService.GenerateRecoveryCodes().Take(2).ToList();
        var bcryptStored = bcryptPlaintext.Select(c => BCrypt.Net.BCrypt.HashPassword(c, workFactor: 10)).ToList();

        var argon2idStored = MfaService.HashRecoveryCodes(MfaService.GenerateRecoveryCodes().Take(2).ToList());

        var sha256Plaintext = s_recoveryCodes.Generate();
        var sha256Stored = sha256Plaintext.Select(c => s_recoveryCodes.Hash(c, TestSalt)).ToList();

        var bcryptResult = MfaService.ValidateRecoveryCode(
            s_recoveryCodes, "definitely-not-minted", bcryptStored, TestSalt);
        var sha256Result = MfaService.ValidateRecoveryCode(
            s_recoveryCodes, "definitely-not-minted", sha256Stored, TestSalt);

        var argon2idResult = MfaService.ValidateRecoveryCode(
            s_recoveryCodes, "definitely-not-minted", argon2idStored, TestSalt);

        bcryptResult.Should().Be((false, -1),
            because: "a non-matching code against BCrypt digests is a clean rejection, never a 500");
        argon2idResult.Should().Be((false, -1),
            because: "a non-matching code against Argon2id digests is a clean rejection, never a 500");
        sha256Result.Should().Be((false, -1),
            because: "a non-matching code against salted-SHA-256 digests is a clean rejection, never a 500");
    }

    [Fact]
    public void ValidateRecoveryCode_ShouldReturnFalse_WhenSaltDoesNotMatchTheMintingSalt()
    {
        // IRecoveryCodeService.Hash salts with user.UserId.Value and the salt is
        // not recoverable from the digest. Redemption under any other salt MUST
        // fail — this pins that the handler cannot substitute a different value.
        var plaintext = s_recoveryCodes.Generate();
        var stored = plaintext.Select(c => s_recoveryCodes.Hash(c, TestSalt)).ToList();

        var (isValid, index) = MfaService.ValidateRecoveryCode(
            s_recoveryCodes, plaintext[0], stored, "some-other-user-id");

        isValid.Should().BeFalse(
            because: "a code minted for one user must not redeem under another user's salt");
        index.Should().Be(-1);
    }

    // ─── Third family: Argon2id (harden-recovery-code-digest, Phase A) ──────
    //
    // Every new mint writes Argon2id at the password parameters (design D1, D4). The
    // fixtures are forged by Argon2idTestDigests (Isopoh at the production parameters,
    // over the NORMALISED plaintext, which is what the mint will hash).

    [Fact]
    public void ValidateRecoveryCode_ShouldReturnTrue_WhenCodeMatchesArgon2idDigest()
    {
        var stored = new[] { Argon2idTestDigests.Good };

        var (isValid, index) = MfaService.ValidateRecoveryCode(
            s_recoveryCodes, Argon2idTestDigests.GoodPlaintext, stored, TestSalt);

        isValid.Should().BeTrue(
            because: "an element starting with $argon2id$ must be verified with Argon2id, not fall to the "
                   + "salted-SHA-256 branch");
        index.Should().Be(0);
    }

    [Fact]
    public void ValidateRecoveryCode_ShouldMatchArgon2id_WhenCodeIsTypedLowercaseWithWhitespace()
    {
        // D4: the Argon2id branch normalises the submitted code exactly as the mint does
        // (Trim + ToUpperInvariant), matching what the SHA-256 family has always done.
        var stored = new[] { Argon2idTestDigests.Good };
        var typed = "  " + Argon2idTestDigests.GoodPlaintext.ToLowerInvariant() + " \t";

        var (isValid, index) = MfaService.ValidateRecoveryCode(s_recoveryCodes, typed, stored, TestSalt);

        isValid.Should().BeTrue(
            because: "a code typed in lower case with surrounding whitespace is the same code once normalised");
        index.Should().Be(0);
    }

    [Fact]
    public void ValidateRecoveryCode_ShouldMatchPerElement_WhenArrayMixesThreeFamilies()
    {
        // One element per family; each plaintext must return its own index, so the caller
        // removes exactly that element and the other two stay redeemable.
        const string bcryptPlaintext = "0123456789abcdef"; // /auth/mfa/* format, verified raw
        const string sha256Plaintext = "WIZARDCD";          // profile format

        var stored = new List<string>
        {
            Argon2idTestDigests.Good,
            BCrypt.Net.BCrypt.HashPassword(bcryptPlaintext, workFactor: 10),
            s_recoveryCodes.Hash(sha256Plaintext, TestSalt),
        };

        var argon2idMatch = MfaService.ValidateRecoveryCode(
            s_recoveryCodes, Argon2idTestDigests.GoodPlaintext, stored, TestSalt);
        var bcryptMatch = MfaService.ValidateRecoveryCode(s_recoveryCodes, bcryptPlaintext, stored, TestSalt);
        var sha256Match = MfaService.ValidateRecoveryCode(s_recoveryCodes, sha256Plaintext, stored, TestSalt);

        argon2idMatch.Should().Be((true, 0),
            because: "element 0 is an Argon2id digest and must be verified with Argon2id");
        bcryptMatch.Should().Be((true, 1),
            because: "element 1 is a BCrypt digest, verified with BCrypt even beside an Argon2id element");
        sha256Match.Should().Be((true, 2),
            because: "element 2 is a salted-SHA-256 digest, verified through IRecoveryCodeService");
    }

    [Theory]
    [MemberData(nameof(Argon2idTestDigests.CorruptCases), MemberType = typeof(Argon2idTestDigests))]
    public void ValidateRecoveryCode_ShouldReturnFalse_WhenArgon2idDigestIsCorrupt(string kind)
    {
        // Spec MODIFIED "Stored material never raises": the Argon2id branch must (a) treat any
        // library exception as no match and (b) refuse, WITHOUT evaluating, a stored digest
        // whose encoded cost falls outside a fixed bound. The submitted code is the one the
        // digest was derived from, so only the corruption can make it a miss.
        var stored = new[] { Argon2idTestDigests.Corrupt(kind) };

        var before = GC.GetAllocatedBytesForCurrentThread();
        var act = () => MfaService.ValidateRecoveryCode(
            s_recoveryCodes, Argon2idTestDigests.GoodPlaintext, stored, TestSalt);

        act.Should().NotThrow(
            because: "no content of the stored column may raise out of the verifier");
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        act().Should().Be((false, -1),
            because: "a corrupt Argon2id digest is a failed verification, never a match");

        if (kind == "m=262144")
        {
            allocated.Should().BeLessThan(64L * 1024 * 1024,
                because: "a stored memory cost above the bound must be refused before evaluation; Isopoh "
                       + "honours m=262144 and allocated 2,132 MiB for one verify in the RC0 probe");
        }
    }

    [Fact]
    public void ValidateRecoveryCode_ShouldReturnFalse_WhenSubmittedCodeIsLongerThan64Characters()
    {
        // Design D2: a submitted code longer than 64 characters after trimming is a miss with no
        // evaluation, so one attempt cannot make the verifier hash an arbitrarily large input ten
        // times. The stored element is the real SHA-256 digest of that same long code, so only the
        // length bound can make it a miss.
        var tooLong = new string('A', 65);
        var stored = new[] { s_recoveryCodes.Hash(tooLong, TestSalt) };

        MfaService.ValidateRecoveryCode(s_recoveryCodes, tooLong, stored, TestSalt).Should().Be((false, -1),
            because: "a code over the length bound is refused before any digest is evaluated");

        var atLimit = new string('A', 64);
        var storedAtLimit = new[] { s_recoveryCodes.Hash(atLimit, TestSalt) };
        MfaService.ValidateRecoveryCode(s_recoveryCodes, "  " + atLimit + " ", storedAtLimit, TestSalt)
            .Should().Be((true, 0), because: "the bound is measured after trimming and admits 64 characters");
    }
}
