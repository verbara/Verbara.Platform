using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Npgsql;
using Verbara.Platform.Mail.Services;

namespace Verbara.Platform.Mail.Tests.Services;

/// <summary>
/// csat-email-replies-forwarded-once (#696): <see cref="PostgresImapPollPositionStore"/> against a Postgres built from
/// the real migration 034 (copied next to the test assembly from Storage.Postgres/Migrations), applied twice.
/// </summary>
public sealed class PostgresImapPollPositionStoreTests : IClassFixture<ImapPollPositionsFixture>
{
    private readonly ImapPollPositionsFixture _fixture;

    public PostgresImapPollPositionStoreTests(ImapPollPositionsFixture fixture) => _fixture = fixture;

    private static ImapMailboxOptions Mailbox(string tenant, string username = "csat@example.test", int port = 993) => new()
    {
        TenantId = tenant,
        Host = "imap.example.test",
        Port = port,
        Username = username,
        Password = "unused",
    };

    [Fact]
    public async Task GetAsync_ShouldReturnNull_WhenTheMailboxWasNeverRead()
    {
        var store = new PostgresImapPollPositionStore(_fixture.DataSource);

        (await store.GetAsync(Mailbox("t-never"), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task SaveAsync_ShouldKeepTheLatestPositionForANewStoreInstance_WhenSavedTwice()
    {
        var mailbox = Mailbox("t-save");
        await new PostgresImapPollPositionStore(_fixture.DataSource)
            .SaveAsync(mailbox, new ImapPollPosition(7, 3), CancellationToken.None);
        await new PostgresImapPollPositionStore(_fixture.DataSource)
            .SaveAsync(mailbox, new ImapPollPosition(7, 4), CancellationToken.None);

        var read = await new PostgresImapPollPositionStore(_fixture.DataSource).GetAsync(mailbox, CancellationToken.None);

        read.Should().Be(new ImapPollPosition(7, 4));
    }

    [Fact]
    public async Task SaveAsync_ShouldKeepEachMailboxApart_WhenTheyDifferOnlyInLoginOrPort()
    {
        var store = new PostgresImapPollPositionStore(_fixture.DataSource);
        await store.SaveAsync(Mailbox("t-apart"), new ImapPollPosition(1, 10), CancellationToken.None);
        await store.SaveAsync(Mailbox("t-apart", username: "other@example.test"), new ImapPollPosition(2, 20), CancellationToken.None);
        await store.SaveAsync(Mailbox("t-apart", port: 143), new ImapPollPosition(3, 30), CancellationToken.None);

        (await store.GetAsync(Mailbox("t-apart"), CancellationToken.None)).Should().Be(new ImapPollPosition(1, 10));
        (await store.GetAsync(Mailbox("t-apart", username: "other@example.test"), CancellationToken.None))
            .Should().Be(new ImapPollPosition(2, 20));
        (await store.GetAsync(Mailbox("t-apart", port: 143), CancellationToken.None)).Should().Be(new ImapPollPosition(3, 30));
        (await store.GetAsync(Mailbox("T-APART"), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task SaveAsync_ShouldRoundTripTheLargestUid_WhenTheValueNeedsAll32Bits()
    {
        var store = new PostgresImapPollPositionStore(_fixture.DataSource);
        await store.SaveAsync(Mailbox("t-max"), new ImapPollPosition(uint.MaxValue, uint.MaxValue), CancellationToken.None);

        (await store.GetAsync(Mailbox("t-max"), CancellationToken.None))
            .Should().Be(new ImapPollPosition(uint.MaxValue, uint.MaxValue));
    }
}

/// <summary>
/// An empty <c>postgres:16-alpine</c> with migration 034 applied twice (it must be idempotent), the way the Api's
/// migration runner applies it to a database the mail service shares.
/// </summary>
public sealed class ImapPollPositionsFixture : IAsyncLifetime
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(3);
    private IContainer? _container;

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    private string ConnectionString =>
        $"Host={_container!.Hostname};Port={_container.GetMappedPublicPort(5432)};" +
        "Database=postgres;Username=postgres;Password=postgres";

    public async Task InitializeAsync()
    {
        _container = new ContainerBuilder("postgres:16-alpine")
            .WithEnvironment("POSTGRES_PASSWORD", "postgres")
            .WithEnvironment("POSTGRES_DB", "postgres")
            .WithPortBinding(5432, true)
            .WithWaitStrategy(Wait.ForUnixContainer()
                // TCP probe: the entrypoint's temporary server listens on the Unix socket only.
                .UntilCommandIsCompleted("pg_isready", "-U", "postgres", "-h", "127.0.0.1"))
            .Build();
        using var startTimeout = new CancellationTokenSource(StartTimeout);
        await _container.StartAsync(startTimeout.Token);

        var migration = await File.ReadAllTextAsync(Path.Join(AppContext.BaseDirectory, "Migrations", "034_MailImapPollPositions.sql"));
        const int maxAttempts = 10;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var conn = new NpgsqlConnection(ConnectionString);
                await conn.OpenAsync();
                for (var run = 0; run < 2; run++)
                {
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = migration;
                    await cmd.ExecuteNonQueryAsync();
                }
                break;
            }
            catch (NpgsqlException) when (attempt < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt)); // fence-allow: GUARD-TIMEOUT — backoff between connect retries while the Postgres container comes up
            }
        }

        DataSource = NpgsqlDataSource.Create(ConnectionString);
    }

    public async Task DisposeAsync()
    {
        if (DataSource is not null) await DataSource.DisposeAsync();
        if (_container is not null) await _container.DisposeAsync();
    }
}
