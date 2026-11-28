using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using MailKit.Net.Imap;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Verbara.Platform.Mail.Services;
using Verbara.Platform.Mail.Tests.Hosting;
using Verbara.Sdk.Pro.CsatRunner.Tokens;

namespace Verbara.Platform.Mail.Tests.Services;

/// <summary>
/// mail-waits-for-poll-position-table (#702): after an upgrade the mail service can start before the Api has applied
/// migration 034, so <c>mail.imap_poll_positions</c> is not there yet. Each test runs the real poll loop over the
/// real <see cref="PostgresImapPollPositionStore"/>, on a database of its own in one Postgres container.
/// </summary>
public sealed class ImapInboundPollerDatabaseUpgradeTests : IClassFixture<UpgradingDatabaseFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 12, 00, 00, TimeSpan.Zero);
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    private const string WaitingLine = "waiting for the database to be upgraded";

    private readonly UpgradingDatabaseFixture _fixture;
    private readonly HmacCsatReplyTokenSigner _signer = new("test-signing-secret-0123456789abcdef", TimeSpan.FromDays(7));

    public ImapInboundPollerDatabaseUpgradeTests(UpgradingDatabaseFixture fixture) => _fixture = fixture;

    private static readonly ImapMailboxOptions Mailbox = new()
    {
        TenantId = "ten-42",
        Host = "localhost",
        Username = "csat@tenant.verbara.io",
        Password = "throwaway",
    };

    [Theory]
    [InlineData(true)]  // an upgrade: the mail schema is there (migration 001), the table of migration 034 is not
    [InlineData(false)] // a new deployment polled before the Api created anything
    public async Task ExecuteAsync_ShouldLogOneInformationLineAndNoError_WhileThePollPositionTableIsMissing(bool withMailSchema)
    {
        await using var db = await _fixture.CreateDatabaseAsync(withMailSchema);
        var capture = new LogRecordCapture();
        using var factory = LoggerFactory.Create(b => b.AddProvider(capture));
        var server = new ImapInboundPollerForwardOnceTests.FakeMailbox(uidValidity: 7);
        var connects = 0;
        var poller = NewPoller(new ImapInboundPollerForwardOnceTests.RecordingForwarder(), server, db.DataSource, factory,
            () => Interlocked.Increment(ref connects));

        await poller.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => Volatile.Read(ref connects) >= 4, "the poller keeps polling while it waits");
        await poller.StopAsync(CancellationToken.None);

        var records = PollerRecords(capture);
        records.Should().NotContain(r => r.Level >= LogLevel.Warning, "a missing table is an upgrade in progress, not a failure");
        records.Should().NotContain(r => r.Exception != null, "no stack is written while the database is upgraded");
        records.Where(r => r.Message.Contains(WaitingLine, StringComparison.Ordinal)).Should().ContainSingle()
            .Which.Should().Match<CapturedLogRecord>(r => r.Level == LogLevel.Information
                && r.Message.Contains("tenant=ten-42", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteAsync_ShouldForwardTheRepliesThatArrivedWhileItWaited_WhenTheTableIsCreated()
    {
        await using var db = await _fixture.CreateDatabaseAsync(withMailSchema: true);
        var capture = new LogRecordCapture();
        using var factory = LoggerFactory.Create(b => b.AddProvider(capture));
        var server = new ImapInboundPollerForwardOnceTests.FakeMailbox(uidValidity: 7);
        server.Deliver(rating: 1); // forwarded by the previous release before the upgrade
        var forwarder = new ImapInboundPollerForwardOnceTests.RecordingForwarder();
        var connects = 0;
        var poller = NewPoller(forwarder, server, db.DataSource, factory, () => Interlocked.Increment(ref connects));

        await poller.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => Volatile.Read(ref connects) >= 2, "the poller polls while the table is missing");
        server.Deliver(rating: 2); // arrives while the mail service waits for the Api to migrate
        var afterDelivery = Volatile.Read(ref connects);
        await WaitUntilAsync(() => Volatile.Read(ref connects) >= afterDelivery + 2, "the poller sees the new reply while waiting");

        await db.ApplyMigration034Async();
        await WaitUntilAsync(() => forwarder.Forwarded.Count >= 1, "polling resumes once the table exists");
        server.Deliver(rating: 3);
        await WaitUntilAsync(() => forwarder.Forwarded.Count >= 2, "the next reply is read on a later poll");
        var afterResume = Volatile.Read(ref connects);
        await WaitUntilAsync(() => Volatile.Read(ref connects) >= afterResume + 2, "later polls forward nothing again");
        await poller.StopAsync(CancellationToken.None);

        forwarder.Forwarded.Should().Equal([2, 3], "the reply that arrived during the wait is forwarded once, the older one never");
        PollerRecords(capture).Should().NotContain(r => r.Level >= LogLevel.Warning);
        (await new PostgresImapPollPositionStore(db.DataSource).GetAsync(Mailbox, CancellationToken.None))
            .Should().Be(new ImapPollPosition(7, 3));
    }

    [Fact]
    public async Task ExecuteAsync_ShouldStillLogAWarningWithTheErrorOnEveryPoll_WhenTheDatabaseFailsOtherwise()
    {
        // The table is there but not the one migration 034 creates: Postgres answers 42703 undefined_column.
        await using var db = await _fixture.CreateDatabaseAsync(withMailSchema: true);
        await using (var cmd = db.DataSource.CreateCommand("CREATE TABLE mail.imap_poll_positions (unrelated INTEGER)"))
            await cmd.ExecuteNonQueryAsync();
        var capture = new LogRecordCapture();
        using var factory = LoggerFactory.Create(b => b.AddProvider(capture));
        var server = new ImapInboundPollerForwardOnceTests.FakeMailbox(uidValidity: 7);
        var poller = NewPoller(new ImapInboundPollerForwardOnceTests.RecordingForwarder(), server, db.DataSource, factory, () => { });

        await poller.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => PollerRecords(capture).Count(r => r.Level == LogLevel.Warning) >= 3, "each failed poll is logged");
        await poller.StopAsync(CancellationToken.None);

        var warnings = PollerRecords(capture).Where(r => r.Level == LogLevel.Warning).ToList();
        warnings.Should().OnlyContain(r => r.Message.StartsWith("IMAP poll failed", StringComparison.Ordinal)
                                           && r.Exception != null && r.Exception.Contains("42703", StringComparison.Ordinal));
        PollerRecords(capture).Should().NotContain(r => r.Message.Contains(WaitingLine, StringComparison.Ordinal));
    }

    private ImapInboundPoller NewPoller(
        ImapInboundPollerForwardOnceTests.RecordingForwarder forwarder, ImapInboundPollerForwardOnceTests.FakeMailbox server,
        NpgsqlDataSource dataSource, ILoggerFactory factory, Action onConnect)
    {
        var options = new ImapPollerOptions { Enabled = true, PollInterval = TimeSpan.FromMilliseconds(100) };
        options.Mailboxes.Add(Mailbox);
        return new ImapInboundPoller(
            Options.Create(options), NewHandler(forwarder), new PostgresImapPollPositionStore(dataSource),
            factory.CreateLogger<ImapInboundPoller>(),
            _ =>
            {
                onConnect();
                return Task.FromResult<IImapClient>(server.Connect());
            });
    }

    private CsatReplyMailHandler NewHandler(ImapInboundPollerForwardOnceTests.RecordingForwarder forwarder) =>
        new(_signer, new AlwaysResolve(), forwarder, timeProvider: new FixedTime(Now));

    private static List<CapturedLogRecord> PollerRecords(LogRecordCapture capture) =>
        [.. capture.Records.Where(r => r.Category == typeof(ImapInboundPoller).FullName)];

    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + Deadline;
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(50); // fence-allow: LOOP-DRIVER — bounded poll on the poller's counters and log, which raise no signal of their own
        condition().Should().BeTrue(because);
    }

    private sealed class AlwaysResolve : ICsatEmailDispatchResolver
    {
        private static readonly CsatEmailDispatch Dispatch = new("ten-42", "srv-csat-v1", "support-tier1", "conv-1");
        public Task<CsatEmailDispatch?> ResolveByTokenAsync(string token, CancellationToken ct) => Task.FromResult<CsatEmailDispatch?>(Dispatch);
        public Task<CsatEmailDispatch?> ResolveByInReplyToAsync(string inReplyToMessageId, CancellationToken ct) => Task.FromResult<CsatEmailDispatch?>(Dispatch);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

/// <summary>
/// One <c>postgres:16-alpine</c> container; each test gets a database of its own, with the mail schema or without it,
/// and never the table of migration 034 until the test applies it.
/// </summary>
public sealed class UpgradingDatabaseFixture : IAsyncLifetime
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(3);
    private IContainer? _container;

    private string ConnectionString(string database) =>
        $"Host={_container!.Hostname};Port={_container.GetMappedPublicPort(5432)};" +
        $"Database={database};Username=postgres;Password=postgres";

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

        const int maxAttempts = 10;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var conn = new NpgsqlConnection(ConnectionString("postgres"));
                await conn.OpenAsync();
                break;
            }
            catch (NpgsqlException) when (attempt < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt)); // fence-allow: GUARD-TIMEOUT — backoff between connect retries while the Postgres container comes up
            }
        }
    }

    public async Task<UpgradingDatabase> CreateDatabaseAsync(bool withMailSchema)
    {
        var name = $"upgrade_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(ConnectionString("postgres")))
        {
            await admin.OpenAsync();
            await using var create = admin.CreateCommand();
            create.CommandText = $"CREATE DATABASE {name}";
            await create.ExecuteNonQueryAsync();
        }

        var dataSource = NpgsqlDataSource.Create(ConnectionString(name));
        if (withMailSchema)
        {
            await using var cmd = dataSource.CreateCommand("CREATE SCHEMA mail");
            await cmd.ExecuteNonQueryAsync();
        }

        return new UpgradingDatabase(dataSource);
    }

    public async Task DisposeAsync()
    {
        if (_container is not null) await _container.DisposeAsync();
    }
}

/// <summary>A database the Api has not migrated to 034 yet.</summary>
public sealed class UpgradingDatabase(NpgsqlDataSource dataSource) : IAsyncDisposable
{
    public NpgsqlDataSource DataSource { get; } = dataSource;

    /// <summary>What the Api's migration runner does once it reaches 034.</summary>
    public async Task ApplyMigration034Async()
    {
        var migration = await File.ReadAllTextAsync(Path.Join(AppContext.BaseDirectory, "Migrations", "034_MailImapPollPositions.sql"));
        await using var cmd = DataSource.CreateCommand(migration);
        await cmd.ExecuteNonQueryAsync();
    }

    public ValueTask DisposeAsync() => DataSource.DisposeAsync();
}
