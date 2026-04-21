using Asterisk.Platform.Core.Email;
using Asterisk.Platform.Mail.Services;
using Asterisk.Sdk.Resilience;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Asterisk.Platform.Mail.Tests.Services;

public sealed class SmtpSenderTests
{
    private static readonly SmtpOptions Options = new()
    {
        Host = "smtp.example.com",
        Port = 587,
        UseTls = true,
        FromAddress = "noreply@example.com",
        FromName = "Test Platform",
    };

    private static EmailMessage BuildMessage() => new()
    {
        Subject = "Hello",
        TextBody = "body",
        Recipients = new[] { new EmailRecipient("to@example.com", "Recipient") },
    };

    [Fact]
    public async Task SendAsync_ShouldInvokeDelegateOnce_WhenSendSucceeds()
    {
        var invocations = 0;
        Func<MimeMessage, CancellationToken, Task> sendMime = (_, _) =>
        {
            Interlocked.Increment(ref invocations);
            return Task.CompletedTask;
        };

        var policy = new ResiliencePolicyBuilder()
            .WithRetry(maxAttempts: 2, baseDelay: TimeSpan.FromMilliseconds(1))
            .Build();

        var sut = new SmtpSender(
            MsOptions.Create(Options),
            NullLogger<SmtpSender>.Instance,
            policy,
            sendMime);

        await sut.SendAsync(BuildMessage(), CancellationToken.None);

        invocations.Should().Be(1);
    }

    [Fact]
    public async Task SendAsync_ShouldRetryAndSucceed_WhenFirstAttemptThrowsTransientException()
    {
        var invocations = 0;
        Func<MimeMessage, CancellationToken, Task> sendMime = (_, _) =>
        {
            var current = Interlocked.Increment(ref invocations);
            if (current == 1)
                throw new IOException("transient SMTP blip");
            return Task.CompletedTask;
        };

        var policy = new ResiliencePolicyBuilder()
            .WithRetry(maxAttempts: 2, baseDelay: TimeSpan.FromMilliseconds(1))
            .Build();

        var sut = new SmtpSender(
            MsOptions.Create(Options),
            NullLogger<SmtpSender>.Instance,
            policy,
            sendMime);

        await sut.SendAsync(BuildMessage(), CancellationToken.None);

        invocations.Should().Be(2, "first attempt throws, retry succeeds");
    }

    [Fact]
    public async Task SendAsync_ShouldPropagateException_WhenRetriesExhausted()
    {
        var invocations = 0;
        Func<MimeMessage, CancellationToken, Task> sendMime = (_, _) =>
        {
            Interlocked.Increment(ref invocations);
            throw new IOException("persistent failure");
        };

        var policy = new ResiliencePolicyBuilder()
            .WithRetry(maxAttempts: 2, baseDelay: TimeSpan.FromMilliseconds(1))
            .Build();

        var sut = new SmtpSender(
            MsOptions.Create(Options),
            NullLogger<SmtpSender>.Instance,
            policy,
            sendMime);

        var act = () => sut.SendAsync(BuildMessage(), CancellationToken.None).AsTask();

        await act.Should().ThrowAsync<IOException>().WithMessage("persistent failure");
        invocations.Should().Be(2, "both attempts run before giving up");
    }

    [Fact]
    public void ResiliencePolicyKey_ShouldMatchContractedName()
    {
        // Guards against accidental rename — keyed singleton registered in Program.cs
        // and consumed by WebhookDeliveryService/OidcTokenExchangeService rely on this
        // contract being stable across Pro/Platform.
        SmtpSender.ResiliencePolicyKey.Should().Be("smtp.send");
    }
}
