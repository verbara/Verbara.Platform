using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Verbara.Platform.Mail.Tests.Services;

/// <summary>
/// Testcontainers-backed MailHog fixture for the CSAT email reply end-to-end (csat-runner Phase C).
/// MailHog captures SMTP submissions and exposes them over an HTTP API — the end-to-end test sends a
/// real CSAT reply over SMTP (port 1025) and reads the stored raw MIME back over the HTTP API
/// (port 8025), so <see cref="Verbara.Platform.Mail.Services.CsatReplyMailHandler"/> is exercised
/// against a message that made a genuine SMTP round-trip rather than a hand-built in-memory object.
/// </summary>
public sealed class MailHogFixture : IAsyncLifetime
{
    private IContainer? _container;

    public string SmtpHost => _container!.Hostname;
    public int SmtpPort => _container!.GetMappedPublicPort(1025);
    public int HttpPort => _container!.GetMappedPublicPort(8025);

    public async Task InitializeAsync()
    {
        _container = new ContainerBuilder("mailhog/mailhog:latest")
            .WithPortBinding(1025, true)
            .WithPortBinding(8025, true)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r.ForPort(8025).ForPath("/api/v2/messages")))
            .Build();

        await _container.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null) await _container.DisposeAsync();
    }
}

#pragma warning disable CA1711 // Identifiers should not have incorrect suffix - xunit convention
[CollectionDefinition("MailHog")]
public class MailHogCollection : ICollectionFixture<MailHogFixture>;
#pragma warning restore CA1711
