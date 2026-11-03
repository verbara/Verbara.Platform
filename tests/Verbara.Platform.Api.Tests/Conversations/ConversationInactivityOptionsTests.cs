using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Api.Tests.Logging;
using Verbara.Platform.Conversations;

namespace Verbara.Platform.Api.Tests.Conversations;

/// <summary>
/// conversations-close-after-inactivity task 1.2 (design D1): the four <c>Conversations:InactivityClose*</c> options,
/// their defaults, their binding from the <c>Conversations</c> section (explicit <c>TryParse</c>, AOT-safe), the
/// out-of-range values that fail startup naming the key, and the one startup warning for a host window below
/// WhatsApp's 24 h customer-service window. The host cases start the real <c>Program</c>.
/// </summary>
public sealed class ConversationInactivityOptionsTests
{
    private const int WindowBelowWhatsAppEventId = 7600;

    [Fact]
    public void ConversationOptions_ShouldDefaultToEnabled72Hours60SecondsAndBatch100_WhenNothingIsConfigured()
    {
        var options = new ConversationOptions();

        options.InactivityCloseEnabled.Should().BeTrue();
        options.InactivityCloseAfterHours.Should().Be(72);
        options.InactivityCloseSweepIntervalSeconds.Should().Be(60);
        options.InactivityCloseBatchSize.Should().Be(100);
    }

    [Fact]
    public void Bind_ShouldReadAllFourKeys_WhenTheConversationsSectionSetsThem()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["Conversations:InactivityCloseEnabled"] = "false",
            ["Conversations:InactivityCloseAfterHours"] = "48",
            ["Conversations:InactivityCloseSweepIntervalSeconds"] = "300",
            ["Conversations:InactivityCloseBatchSize"] = "250",
        });
        var options = new ConversationOptions();

        ConversationOptionsSetup.Bind(configuration.GetSection(ConversationOptionsSetup.SectionName), options);

        options.InactivityCloseEnabled.Should().BeFalse();
        options.InactivityCloseAfterHours.Should().Be(48);
        options.InactivityCloseSweepIntervalSeconds.Should().Be(300);
        options.InactivityCloseBatchSize.Should().Be(250);
    }

    [Fact]
    public void Host_ShouldResolveTheBoundOptions_WhenTheSectionIsSet()
    {
        using var factory = Factory(new Dictionary<string, string?>
        {
            ["Conversations:InactivityCloseEnabled"] = "false",
            ["Conversations:InactivityCloseAfterHours"] = "96",
            ["Conversations:InactivityCloseSweepIntervalSeconds"] = "120",
            ["Conversations:InactivityCloseBatchSize"] = "10",
        });

        var options = factory.Services.GetRequiredService<IOptions<ConversationOptions>>().Value;

        options.InactivityCloseEnabled.Should().BeFalse();
        options.InactivityCloseAfterHours.Should().Be(96);
        options.InactivityCloseSweepIntervalSeconds.Should().Be(120);
        options.InactivityCloseBatchSize.Should().Be(10);
    }

    [Theory]
    [InlineData("Conversations:InactivityCloseAfterHours", "0")]
    [InlineData("Conversations:InactivityCloseSweepIntervalSeconds", "4")]
    [InlineData("Conversations:InactivityCloseSweepIntervalSeconds", "3601")]
    [InlineData("Conversations:InactivityCloseBatchSize", "0")]
    [InlineData("Conversations:InactivityCloseBatchSize", "1001")]
    public void Host_ShouldFailStartupNamingTheKey_WhenAValueIsOutOfRange(string key, string value)
    {
        using var factory = Factory(new Dictionary<string, string?> { [key] = value });

        var start = () => factory.Services;

        start.Should().Throw<Exception>()
            .Where(e => Flatten(e).Contains(key, StringComparison.Ordinal),
                "the startup failure names the key {0} so the operator knows what to fix", key);
    }

    [Fact]
    public void Host_ShouldLogExactlyOneWhatsAppWarning_WhenTheHostWindowIs12Hours()
    {
        var capture = new LogRecordCapture();
        using (var factory = Factory(
                   new Dictionary<string, string?> { ["Conversations:InactivityCloseAfterHours"] = "12" }, capture))
        {
            _ = factory.Services;
        }

        var warning = capture.Records.Where(r => r.EventId.Id == WindowBelowWhatsAppEventId).Should().ContainSingle(
            "a host window below 24 h starts, with one warning").Subject;
        warning.Level.Should().Be(LogLevel.Warning);
        warning.Message.Should().Contain("Conversations:InactivityCloseAfterHours")
            .And.Contain("WhatsApp").And.Contain("24");
    }

    [Fact]
    public void Host_ShouldLogNoWhatsAppWarning_WhenTheHostWindowIs24Hours()
    {
        var capture = new LogRecordCapture();
        using (var factory = Factory(
                   new Dictionary<string, string?> { ["Conversations:InactivityCloseAfterHours"] = "24" }, capture))
        {
            _ = factory.Services;
        }

        capture.Records.Should().Contain(
            r => r.Category.StartsWith("Microsoft.Hosting.Lifetime", StringComparison.Ordinal)
                || r.Category.StartsWith("Verbara", StringComparison.Ordinal),
            "the capture must see the records written while the host started");
        capture.Records.Should().NotContain(r => r.EventId.Id == WindowBelowWhatsAppEventId);
    }

    private static IConfiguration Configuration(IReadOnlyDictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Factory(
        IReadOnlyDictionary<string, string?> settings, LogRecordCapture? capture = null) =>
        new PlatformApiFactory().WithWebHostBuilder(builder =>
        {
            foreach (var (key, value) in settings)
                builder.UseSetting(key, value);
            if (capture is not null)
                builder.ConfigureLogging(logging => logging.AddProvider(capture));
        });

    private static string Flatten(Exception e)
    {
        var text = new System.Text.StringBuilder();
        for (Exception? current = e; current is not null; current = current.InnerException)
            text.AppendLine(current.Message);
        if (e is AggregateException aggregate)
            foreach (var inner in aggregate.InnerExceptions)
                text.AppendLine(Flatten(inner));
        return text.ToString();
    }
}
