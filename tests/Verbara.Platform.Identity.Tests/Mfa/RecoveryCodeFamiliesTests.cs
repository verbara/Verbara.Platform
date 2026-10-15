using Verbara.Platform.Identity.Mfa;

namespace Verbara.Platform.Identity.Tests.Mfa;

/// <summary>
/// Prefix-only classification of a stored recovery-code digest (<see cref="RecoveryCodeFamilies"/>).
/// The same function drives verification dispatch, the <c>/users/me</c> flag, the census and the
/// redemption counter, so its fallback must match the dispatch: anything not recognised is the salted
/// SHA-256 family, and therefore legacy.
/// </summary>
public sealed class RecoveryCodeFamiliesTests
{
    private const string Argon2id = "$argon2id$v=19$m=12288,t=3,p=1$c2FsdHNhbHRzYWx0c2FsdA$aGFzaGhhc2hoYXNoaGFzaGhhc2g";
    private const string Bcrypt2a = "$2a$10$abcdefghijklmnopqrstuuJ0123456789abcdefghijklmnopqrstuv";
    private const string Bcrypt2b = "$2b$10$abcdefghijklmnopqrstuuJ0123456789abcdefghijklmnopqrstuv";
    private const string Sha256 = "0F1E2D3C4B5A69788796A5B4C3D2E1F00F1E2D3C4B5A69788796A5B4C3D2E1F0";

    [Theory]
    [InlineData(Argon2id, RecoveryCodeFamily.Argon2id)]
    [InlineData(Bcrypt2a, RecoveryCodeFamily.Bcrypt)]
    [InlineData(Bcrypt2b, RecoveryCodeFamily.Bcrypt)]
    [InlineData(Sha256, RecoveryCodeFamily.Sha256)]
    public void Classify_ShouldReturnFamilyByPrefix_WhenDigestIsWellFormed(string digest, RecoveryCodeFamily expected)
    {
        RecoveryCodeFamilies.Classify(digest).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("$argon2i$v=19$m=12288,t=3,p=1$salt$tag")]
    [InlineData("$ARGON2ID$v=19$m=12288,t=3,p=1$salt$tag")]
    [InlineData("argon2id$v=19")]
    [InlineData("$")]
    [InlineData("not-a-digest")]
    public void Classify_ShouldReturnSha256_WhenDigestIsUnrecognisedOrCorrupt(string digest)
    {
        RecoveryCodeFamilies.Classify(digest).Should().Be(RecoveryCodeFamily.Sha256,
            because: "the dispatch sends anything unrecognised to the salted-SHA-256 branch, and an operator "
                   + "count reads a corrupt value as legacy");
    }

    [Fact]
    public void Classify_ShouldReturnSha256_WhenDigestIsNull()
    {
        RecoveryCodeFamilies.Classify(null).Should().Be(RecoveryCodeFamily.Sha256);
    }

    [Fact]
    public void Classify_ShouldReturnArgon2id_WhenDigestIsPrefixOnly()
    {
        // Classification is prefix-only, with no cryptography; whether the digest verifies is the
        // verifier's question (Argon2idHasher.SafeVerify refuses this value).
        RecoveryCodeFamilies.Classify("$argon2id$").Should().Be(RecoveryCodeFamily.Argon2id);
    }

    [Fact]
    public void HoldsLegacy_ShouldReturnTrue_WhenAnyElementIsLegacy()
    {
        RecoveryCodeFamilies.HoldsLegacy([Argon2id, Argon2id, Bcrypt2a]).Should().BeTrue();
        RecoveryCodeFamilies.HoldsLegacy([Sha256]).Should().BeTrue();
    }

    [Fact]
    public void HoldsLegacy_ShouldReturnFalse_WhenEveryElementIsArgon2id()
    {
        RecoveryCodeFamilies.HoldsLegacy([Argon2id, Argon2id]).Should().BeFalse();
    }

    [Fact]
    public void HoldsLegacy_ShouldReturnFalse_WhenArrayIsNullOrEmpty()
    {
        RecoveryCodeFamilies.HoldsLegacy(null).Should().BeFalse();
        RecoveryCodeFamilies.HoldsLegacy([]).Should().BeFalse();
    }
}
