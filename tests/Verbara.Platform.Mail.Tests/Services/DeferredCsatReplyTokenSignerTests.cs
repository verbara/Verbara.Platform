using Microsoft.Extensions.Options;
using Verbara.Platform.Mail.Services;
using Verbara.Sdk.Pro.CsatRunner.Tokens;

namespace Verbara.Platform.Mail.Tests.Services;

/// <summary>
/// The mail service's reply-token signer is Pro's HMAC signer built from <c>Imap:TokenSigningSecret</c> at its first
/// use (#690): creating it reads no secret, so a host with the reply mailbox off starts without one, and checking a
/// reply without one fails naming the setting.
/// </summary>
public sealed class DeferredCsatReplyTokenSignerTests
{
    private const string Secret = "deferred-signer-tests-secret-0123456789";
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 12, 00, 00, TimeSpan.Zero);

    private static DeferredCsatReplyTokenSigner NewSigner(string secret, TimeSpan? ttl = null)
    {
        var options = new ImapPollerOptions { TokenSigningSecret = secret };
        if (ttl is { } value)
            options.TokenTtl = value;
        return new DeferredCsatReplyTokenSigner(Options.Create(options));
    }

    [Fact]
    public void Constructor_ShouldNotThrow_WhenNoSecretIsSet()
    {
        var create = () => NewSigner(string.Empty);

        create.Should().NotThrow(because: "the secret is read the first time a reply token is checked");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Verify_ShouldThrowNamingTheSetting_WhenNoSecretIsSet(string secret)
    {
        var sut = NewSigner(secret);

        var verify = () => sut.Verify("any-token", Now);

        verify.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("Imap__TokenSigningSecret").And.Contain("Csat:Email:TokenSigningSecret");
    }

    [Fact]
    public void Sign_ShouldThrowNamingTheSetting_WhenNoSecretIsSet()
    {
        var sut = NewSigner(string.Empty);

        var sign = () => sut.Sign("ten-1", "conv-1", "resp-1", Now);

        sign.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("Imap__TokenSigningSecret");
    }

    [Fact]
    public void Verify_ShouldReturnTheBoundFields_WhenTheTokenIsSignedWithTheSameSecret()
    {
        var token = new HmacCsatReplyTokenSigner(Secret, TimeSpan.FromDays(7)).Sign("ten-1", "conv-1", "resp-1", Now);

        var verified = NewSigner(Secret).Verify(token, Now.AddHours(1));

        verified.Should().NotBeNull();
        verified!.TenantId.Should().Be("ten-1");
        verified.ConversationId.Should().Be("conv-1");
        verified.SurveyResponseId.Should().Be("resp-1");
    }

    [Fact]
    public void Verify_ShouldReturnNull_WhenTheTokenIsSignedWithAnotherSecret()
    {
        var token = new HmacCsatReplyTokenSigner("another-secret-0123456789abcdef", TimeSpan.FromDays(7))
            .Sign("ten-1", "conv-1", "resp-1", Now);

        NewSigner(Secret).Verify(token, Now.AddHours(1)).Should().BeNull();
    }

    [Fact]
    public void Verify_ShouldReturnNull_WhenTheTokenIsOlderThanTheConfiguredTtl()
    {
        var sut = NewSigner(Secret, ttl: TimeSpan.FromDays(1));
        var token = sut.Sign("ten-1", "conv-1", "resp-1", Now);

        sut.Verify(token, Now.AddHours(23)).Should().NotBeNull(because: "the token is inside the configured TTL");
        sut.Verify(token, Now.AddHours(25)).Should().BeNull(because: "the configured TTL is one day, not the default seven");
    }
}
