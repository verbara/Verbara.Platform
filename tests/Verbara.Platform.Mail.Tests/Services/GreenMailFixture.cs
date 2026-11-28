using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using MailKit.Net.Imap;
using MailKit.Security;

namespace Verbara.Platform.Mail.Tests.Services;

/// <summary>
/// Testcontainers-backed GreenMail fixture: a real IMAP server, so the poller's folder lookup runs against the
/// server's own LIST answers rather than a substitute (#705). The image's default options disable authentication,
/// so each test logs in with a user name of its own and gets a mailbox no other test shares.
/// </summary>
public sealed class GreenMailFixture : IAsyncLifetime
{
    private const int ImapPort = 3143;
    private IContainer? _container;

    public string Host => _container!.Hostname;
    public int Port => _container!.GetMappedPublicPort(ImapPort);

    public async Task InitializeAsync()
    {
        _container = new ContainerBuilder("greenmail/standalone:2.1.2")
            .WithPortBinding(ImapPort, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(ImapPort))
            .Build();

        await _container.StartAsync();

        // The port answering inside the container is not proof the host side does: log in once from here
        // before any test runs.
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            try
            {
                using var client = await ConnectAsync("readiness-probe");
                await client.DisconnectAsync(quit: true);
                return;
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(500); // fence-allow: GUARD-TIMEOUT — backoff between login retries while the GreenMail container comes up, bounded by the 60 s deadline
            }
        }
    }

    /// <summary>Connects and logs in as <paramref name="user"/> (any password; authentication is disabled).</summary>
    public async Task<ImapClient> ConnectAsync(string user)
    {
        var client = new ImapClient();
        try
        {
            await client.ConnectAsync(Host, Port, SecureSocketOptions.None);
            await client.AuthenticateAsync(user, "throwaway");
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null) await _container.DisposeAsync();
    }
}

#pragma warning disable CA1711 // Identifiers should not have incorrect suffix - xunit convention
[CollectionDefinition("GreenMail")]
public class GreenMailCollection : ICollectionFixture<GreenMailFixture>;
#pragma warning restore CA1711
