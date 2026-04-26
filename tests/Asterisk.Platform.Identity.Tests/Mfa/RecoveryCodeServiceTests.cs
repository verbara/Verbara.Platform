using System.Linq;
using Asterisk.Platform.Identity.Mfa;

namespace Asterisk.Platform.Identity.Tests.Mfa;

/// <summary>
/// Crypto invariants for <see cref="RecoveryCodeService"/> (R5.2 PA.2).
/// </summary>
public sealed class RecoveryCodeServiceTests
{
    private readonly RecoveryCodeService _svc = new();

    // ─── Generate ─────────────────────────────────────────────────────────────

    [Fact]
    public void Generate_ShouldReturn10Codes_WhenCalled()
    {
        var codes = _svc.Generate();

        codes.Should().HaveCount(10);
    }

    [Fact]
    public void Generate_ShouldReturn8CharCodes_WhenCalled()
    {
        var codes = _svc.Generate();

        codes.Should().AllSatisfy(c => c.Length.Should().Be(8));
    }

    [Fact]
    public void Generate_ShouldReturnUniqueCodes_WhenCalledOnce()
    {
        var codes = _svc.Generate();

        codes.Distinct().Should().HaveCount(10);
    }

    [Fact]
    public void Generate_ShouldUseBase32Alphabet_WhenCalled()
    {
        // RFC 4648 minus 0/1/O/I → uppercase letters except O+I, digits 2-9.
        const string allowed = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

        var codes = _svc.Generate();

        codes.SelectMany(c => c).Should().AllSatisfy(ch =>
            allowed.Should().Contain(ch.ToString(),
                because: "recovery codes must avoid the visually confusable glyphs 0/1/O/I"));
    }

    [Fact]
    public void Generate_ShouldYieldDifferentBatches_WhenCalledTwice()
    {
        var first = _svc.Generate();
        var second = _svc.Generate();

        first.Intersect(second).Should().BeEmpty(
            because: "two generations should produce disjoint sets with overwhelming probability");
    }

    // ─── Hash ────────────────────────────────────────────────────────────────

    [Fact]
    public void Hash_ShouldProduceConsistentHash_GivenCodeAndSalt()
    {
        var hash1 = _svc.Hash("ABCD2345", "user-salt");
        var hash2 = _svc.Hash("ABCD2345", "user-salt");

        hash1.Should().Be(hash2);
    }

    [Fact]
    public void Hash_ShouldDifferAcrossSalts_GivenSameCode()
    {
        var hashA = _svc.Hash("ABCD2345", "salt-a");
        var hashB = _svc.Hash("ABCD2345", "salt-b");

        hashA.Should().NotBe(hashB);
    }

    [Fact]
    public void Hash_ShouldDifferAcrossCodes_GivenSameSalt()
    {
        var hash1 = _svc.Hash("ABCD2345", "user-salt");
        var hash2 = _svc.Hash("WXYZ6789", "user-salt");

        hash1.Should().NotBe(hash2);
    }

    [Fact]
    public void Hash_ShouldNormalizeCase_WhenCodeProvidedInLowercase()
    {
        var upper = _svc.Hash("ABCD2345", "salt");
        var lower = _svc.Hash("abcd2345", "salt");

        lower.Should().Be(upper,
            because: "users may type their recovery code in lowercase");
    }

    [Fact]
    public void Hash_ShouldThrow_WhenCodeIsEmpty()
    {
        Assert.Throws<ArgumentException>(() => _svc.Hash(string.Empty, "salt"));
    }

    [Fact]
    public void Hash_ShouldThrow_WhenSaltIsEmpty()
    {
        Assert.Throws<ArgumentException>(() => _svc.Hash("ABCD2345", string.Empty));
    }

    // ─── Verify ──────────────────────────────────────────────────────────────

    [Fact]
    public void Verify_ShouldReturnTrue_WhenCodeMatches()
    {
        const string code = "ABCD2345";
        const string salt = "user-salt";
        var stored = _svc.Hash(code, salt);

        _svc.Verify(code, salt, stored).Should().BeTrue();
    }

    [Fact]
    public void Verify_ShouldReturnFalse_WhenCodeDoesNotMatch()
    {
        var stored = _svc.Hash("ABCD2345", "salt");

        _svc.Verify("WXYZ6789", "salt", stored).Should().BeFalse();
    }

    [Fact]
    public void Verify_ShouldReturnFalse_WhenSaltDiffers()
    {
        var stored = _svc.Hash("ABCD2345", "salt-a");

        _svc.Verify("ABCD2345", "salt-b", stored).Should().BeFalse();
    }

    [Fact]
    public void Verify_ShouldReturnFalse_WhenCodeIsEmpty()
    {
        var stored = _svc.Hash("ABCD2345", "salt");

        _svc.Verify(string.Empty, "salt", stored).Should().BeFalse();
    }

    [Fact]
    public void Verify_ShouldReturnFalse_WhenStoredHashIsEmpty()
    {
        _svc.Verify("ABCD2345", "salt", string.Empty).Should().BeFalse();
    }
}
