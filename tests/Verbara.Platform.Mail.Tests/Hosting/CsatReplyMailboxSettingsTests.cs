using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Verbara.Platform.Core.Email;
using Verbara.Platform.Mail.Services;
using Verbara.Sdk.Pro.CsatRunner.Tokens;

namespace Verbara.Platform.Mail.Tests.Hosting;

/// <summary>
/// With the CSAT reply mailbox on (<c>Imap:Enabled=true</c>) the mail service reads replies from a mailbox and
/// forwards each rating to the Api. These tests start the real Mail host: it must refuse to start when a setting
/// that path needs is missing, naming it, instead of starting and failing on every reply; and it must send the
/// Api the service key the Api checks without the operator writing that key twice.
/// </summary>
public sealed class CsatReplyMailboxSettingsTests
{
    private const string ServiceKey = "mail-host-settings-tests-service-key";
    private static readonly TimeSpan GuardTimeout = TimeSpan.FromSeconds(30);

    private static Dictionary<string, string?> MailboxOn() => new()
    {
        ["Imap:Enabled"] = "true",
        ["Services:ServiceKey"] = ServiceKey,
        ["CsatCapture:ApiBaseAddress"] = "http://platform-api:5000",
        ["Imap:Mailboxes:0:TenantId"] = "tenant-1",
        // Nothing listens on port 1, so the poller's first connection fails at once and is only logged.
        ["Imap:Mailboxes:0:Host"] = "127.0.0.1",
        ["Imap:Mailboxes:0:Port"] = "1",
        ["Imap:Mailboxes:0:UseTls"] = "false",
        ["Imap:Mailboxes:0:Username"] = "csat@example.test",
        ["Imap:Mailboxes:0:Password"] = "mailbox-password",
        ["Imap:TokenSigningSecret"] = "mail-host-settings-tests-reply-token-secret",
    };

    [Fact]
    public void Start_ShouldRefuseNamingTheForwardAddress_WhenTheMailboxIsOnWithoutIt()
    {
        var settings = MailboxOn();
        settings.Remove("CsatCapture:ApiBaseAddress");

        var message = StartFailure(settings);

        message.Should().Contain("CsatCapture__ApiBaseAddress");
        message.Should().Contain("Imap:Enabled");
    }

    [Fact]
    public void Start_ShouldRefuseNamingTheReplySecret_WhenTheMailboxIsOnWithoutIt()
    {
        var settings = MailboxOn();
        settings.Remove("Imap:TokenSigningSecret");

        var message = StartFailure(settings);

        message.Should().Contain("Imap__TokenSigningSecret");
        message.Should().Contain("Imap:Enabled");
    }

    [Fact]
    public void Start_ShouldRefuseNamingTheReplySecret_WhenTheMailboxIsOnWithABlankOne()
    {
        var settings = MailboxOn();
        settings["Imap:TokenSigningSecret"] = "   ";

        StartFailure(settings).Should().Contain("Imap__TokenSigningSecret");
    }

    [Fact]
    public void Start_ShouldRefuseNothing_WhenTheMailboxIsOnWithEverySetting()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(MailboxOn()).Build();
        using var provider = new ServiceCollection().AddPlatformMail(configuration).BuildServiceProvider();

        var start = () => provider.GetRequiredService<IStartupValidator>().Validate();

        start.Should().NotThrow();
    }

    [Fact]
    public async Task Start_ShouldServeHealthAndSend_WhenTheMailboxIsOffAndNoReplySecretIsSet()
    {
        // No Imap:* setting at all, the reply secret included: a deployment that never reads replies.
        using var factory = new LogCapturingMailFactory(new LogRecordCapture(), new Dictionary<string, string?>());
        using var client = factory.CreateClient();
        using var timeout = new CancellationTokenSource(GuardTimeout);

        using var response = await client.GetAsync(new Uri("/health", UriKind.Relative), timeout.Token);

        response.StatusCode.Should().Be(HttpStatusCode.OK, because: "the host started its hosted services, the reply poller included");
        factory.Services.GetRequiredService<IEmailService>().Should().NotBeNull(because: "sending needs no reply secret");
        var resolve = () => factory.Services.GetRequiredService<ICsatReplyTokenSigner>();
        resolve.Should().NotThrow(because: "the signer is built at first use, and nothing uses it with the mailbox off");
    }

    [Fact]
    public void Start_ShouldRefuseNamingEachMailboxSetting_WhenTheMailboxIsOnWithNoMailbox()
    {
        var settings = MailboxOn();
        foreach (var key in settings.Keys.Where(k => k.StartsWith("Imap:Mailboxes:", StringComparison.Ordinal)).ToList())
            settings.Remove(key);

        var message = StartFailure(settings);

        foreach (var name in new[] { "TenantId", "Host", "Username", "Password" })
            message.Should().Contain($"Imap__Mailboxes__0__{name}");
    }

    [Fact]
    public void Start_ShouldRefuse_WhenTheForwardAddressIsNotAnAbsoluteHttpAddress()
    {
        var settings = MailboxOn();
        settings["CsatCapture:ApiBaseAddress"] = "platform-api:5000";

        StartFailure(settings).Should().Contain("CsatCapture__ApiBaseAddress");
    }

    [Fact]
    public void Start_ShouldRefuseNamingBothKeys_WhenNoServiceKeyIsSetAtAll()
    {
        var settings = MailboxOn();
        settings.Remove("Services:ServiceKey");

        var message = StartFailure(settings);

        message.Should().Contain("CsatCapture__ServiceKey");
        message.Should().Contain("Services__ServiceKey");
    }

    [Fact]
    public void Forwarder_ShouldSendTheServicesServiceKey_WhenNoCaptureKeyIsSet()
    {
        using var factory = new LogCapturingMailFactory(new LogRecordCapture(), MailboxOn());

        var options = factory.Services.GetRequiredService<IOptions<CsatCaptureForwardOptions>>().Value;

        options.ServiceKey.Should().Be(ServiceKey,
            because: "the Api checks the capture call against its own Services:ServiceKey, the same file's value");
    }

    [Fact]
    public void Forwarder_ShouldSendTheCaptureKey_WhenOneIsSet()
    {
        var settings = MailboxOn();
        settings["CsatCapture:ServiceKey"] = "an-explicit-capture-key";
        using var factory = new LogCapturingMailFactory(new LogRecordCapture(), settings);

        var options = factory.Services.GetRequiredService<IOptions<CsatCaptureForwardOptions>>().Value;

        options.ServiceKey.Should().Be("an-explicit-capture-key");
    }

    [Fact]
    public void Start_ShouldSucceed_WhenTheMailboxIsOffAndNoneOfItsSettingsAreSet()
    {
        using var factory = new LogCapturingMailFactory(new LogRecordCapture(), new Dictionary<string, string?>());

        var start = () => factory.Services.GetRequiredService<IOptions<ImapPollerOptions>>().Value;

        start.Should().NotThrow();
    }

    /// <summary>
    /// Runs the checks the host runs when it starts (<see cref="IStartupValidator"/>, fed by <c>ValidateOnStart</c>)
    /// over the services <see cref="ServiceCollectionExtensions.AddPlatformMail"/> registers, and returns every
    /// message of the exception they fail with. Starting a whole test host here raced: a host that fails to start
    /// is disposed while it starts, so the failure sometimes surfaced as "Cannot access a disposed object".
    /// </summary>
    private static string StartFailure(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        using var provider = new ServiceCollection().AddPlatformMail(configuration).BuildServiceProvider();
        var start = () => provider.GetRequiredService<IStartupValidator>().Validate();
        var thrown = start.Should().Throw<Exception>(because: "a host that cannot forward replies must not start").Which;
        var messages = new List<string>();
        for (Exception? e = thrown; e is not null; e = e.InnerException)
        {
            messages.Add(e.Message);
            if (e is AggregateException aggregate)
                messages.AddRange(aggregate.InnerExceptions.SelectMany(inner =>
                    inner is OptionsValidationException v ? v.Failures.Prepend(inner.Message) : [inner.Message]));
            if (e is OptionsValidationException validation)
                messages.AddRange(validation.Failures);
        }
        return string.Join("\n", messages);
    }
}
