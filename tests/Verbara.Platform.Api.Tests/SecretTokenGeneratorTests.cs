using FluentAssertions;
using Verbara.Platform.Api.Services;

namespace Verbara.Platform.Api.Tests;

public sealed class SecretTokenGeneratorTests
{
    [Fact]
    public void Mint_ShouldReturnPrefixedLowercaseHex_WhenPrefixGiven()
    {
        var token = SecretTokenGenerator.Mint("mgmt_");

        token.Should().StartWith("mgmt_");
        var body = token["mgmt_".Length..];
        body.Should().MatchRegex("^[0-9a-f]{64}$"); // 32-byte CSPRNG -> 64 lowercase hex (256-bit)
    }

    [Fact]
    public void Mint_ShouldReturnBareLowercaseHex_WhenNoPrefix()
    {
        SecretTokenGenerator.Mint().Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void Mint_ShouldNotProduceGuidShape_WhenMinting()
    {
        // A regression to `Guid.NewGuid():N` yields 32 hex chars (halved entropy);
        // the CSPRNG secret is 64. Locking the length keeps the weak shape from returning.
        var token = SecretTokenGenerator.Mint();

        token.Length.Should().Be(64);
        token.Should().NotMatchRegex("^[0-9a-f]{32}$");
    }

    [Fact]
    public void Mint_ShouldProduceDistinctValues_WhenCalledRepeatedly()
    {
        var seen = new HashSet<string>();
        for (var i = 0; i < 1000; i++)
            seen.Add(SecretTokenGenerator.Mint());

        seen.Should().HaveCount(1000); // CSPRNG: no collision across 1000 mints
    }
}
