using Asterisk.Platform.Core.Webhooks;
using FluentAssertions;

namespace Asterisk.Platform.Core.Tests.Webhooks;

public class WebhookSignatureServiceTests
{
    [Fact]
    public void ComputeSignature_ShouldReturnConsistentHex_WhenCalledWithSameInputs()
    {
        var sig1 = WebhookSignatureService.ComputeSignature("1712000000", "{}", "secret123");
        var sig2 = WebhookSignatureService.ComputeSignature("1712000000", "{}", "secret123");
        sig1.Should().Be(sig2);
    }

    [Fact]
    public void ComputeSignature_ShouldReturnDifferentValues_WhenSecretDiffers()
    {
        var sig1 = WebhookSignatureService.ComputeSignature("1712000000", "{}", "secret-a");
        var sig2 = WebhookSignatureService.ComputeSignature("1712000000", "{}", "secret-b");
        sig1.Should().NotBe(sig2);
    }

    [Fact]
    public void ComputeSignature_ShouldReturnDifferentValues_WhenTimestampDiffers()
    {
        var sig1 = WebhookSignatureService.ComputeSignature("1712000000", "{}", "secret");
        var sig2 = WebhookSignatureService.ComputeSignature("1712000001", "{}", "secret");
        sig1.Should().NotBe(sig2);
    }

    [Fact]
    public void ComputeSignature_ShouldReturnLowercaseHex()
    {
        var sig = WebhookSignatureService.ComputeSignature("1712000000", "{}", "secret");
        sig.Should().MatchRegex("^[0-9a-f]+$");
    }

    [Fact]
    public void VerifySignature_ShouldReturnTrue_WhenSignatureMatches()
    {
        var sig = WebhookSignatureService.ComputeSignature("1712000000", "{}", "secret");
        WebhookSignatureService.VerifySignature("1712000000", "{}", "secret", sig).Should().BeTrue();
    }

    [Fact]
    public void VerifySignature_ShouldReturnFalse_WhenSignatureTampered()
    {
        var sig = WebhookSignatureService.ComputeSignature("1712000000", "{}", "secret");
        WebhookSignatureService.VerifySignature("1712000000", "{}", "secret", sig + "ff").Should().BeFalse();
    }

    [Fact]
    public void GenerateSecret_ShouldReturn64CharHexString()
    {
        var secret = WebhookSignatureService.GenerateSecret();
        secret.Should().HaveLength(64);
        secret.Should().MatchRegex("^[0-9a-f]+$");
    }

    [Fact]
    public void GenerateSecret_ShouldReturnUniqueValues()
    {
        var s1 = WebhookSignatureService.GenerateSecret();
        var s2 = WebhookSignatureService.GenerateSecret();
        s1.Should().NotBe(s2);
    }
}
