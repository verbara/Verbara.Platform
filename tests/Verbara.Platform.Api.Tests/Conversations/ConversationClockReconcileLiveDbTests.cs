using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using NSubstitute;
using Verbara.Platform.Api.Health;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Services;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Queues.Services;
using Verbara.Platform.Storage.Postgres;
using Verbara.Platform.Switchboard;
using Verbara.Sdk.Pro.MultiTenant;

namespace Verbara.Platform.Api.Tests.Conversations;

/// <summary>
/// conversations-close-after-inactivity task 2.8 (design D2, "Clock reconciliation on every start" and "Limit of the
/// reconcile and the backfill"; spec <c>conversation-inactivity-close</c>): the restart scenarios on a real Postgres.
/// Each test gets a fresh database migrated by the real <see cref="DatabaseMigrationService"/>, the real Postgres
/// stores and conversation service from DI, and a new <see cref="ConversationTimeoutWorker"/> per "process start". An
/// older binary is simulated by plain SQL that stores a message without touching <c>conversations</c>.
/// </summary>
public sealed class ConversationClockReconcileLiveDbTests : IClassFixture<ConversationClockReconcileLiveDbTests.PostgresContainer>
{
    private const string Tenant = "t-reconcile";
    private const string Migration026 = "026_ConversationLastMessageAt.sql";
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly EntityId Agent = EntityId.From("agent-reconcile");

    private readonly PostgresContainer _postgres;

    public ConversationClockReconcileLiveDbTests(PostgresContainer postgres) => _postgres = postgres;

    // Rollback -> re-upgrade: 026 is recorded, so a second migration run does not backfill again; the old binary's
    // message (provider timestamp 1 h ago) is reconciled into the clock before the first sweep lists anything.
    [Fact]
    public async Task InactivityPass_ShouldReconcileBeforeListingAndKeepTheChatOpen_WhenAnOlderBinaryStoredARecentMessage()
    {
        await using var env = await Env.CreateAsync(_postgres);
        var id = await env.SeedConversationAsync(ChannelType.WhatsApp, clock: Now.AddHours(-80));
        await env.InsertOldBinaryMessageAsync(id, MessageDirection.Inbound, MessageDeliveryStatus.Delivered, Now.AddHours(-1));

        (await env.ScalarAsync($"SELECT count(*) FROM _migrations WHERE name = '{Migration026}'")).Should().Be(1L);
        DatabaseMigrationService.ApplyMigrations(env.ConnectionString);
        (await env.ClockAsync(id)).Should().Be(Now.AddHours(-80), "026 is recorded: the second migration run does not re-backfill");

        await env.StartWorkerAndRunAPassAsync(Now);

        (await env.StateAsync(id)).Should().Be(ConversationState.Active);
        (await env.ClockAsync(id)).Should().Be(Now.AddHours(-1), "the first pass reconciled the clock before it listed");
    }

    // The stated limit (audit F1): an old-binary message with a provider timestamp older than the frozen clock counts at
    // that timestamp, so the reconcile leaves the clock and the first pass closes the row. A later change that adds a
    // receipt time must flip this test on purpose.
    [Fact]
    public async Task InactivityPass_ShouldCloseTheChat_WhenTheOlderBinaryMessageCarriesAProviderTimestampOlderThanTheClock()
    {
        await using var env = await Env.CreateAsync(_postgres);
        var id = await env.SeedConversationAsync(ChannelType.WhatsApp, clock: Now.AddHours(-80));
        await env.InsertOldBinaryMessageAsync(id, MessageDirection.Inbound, MessageDeliveryStatus.Delivered, Now.AddHours(-90));

        await env.StartWorkerAndRunAPassAsync(Now);

        (await env.ClockAsync(id)).Should().Be(Now.AddHours(-80), "the message counts at its provider timestamp (90 h)");
        (await env.StateAsync(id)).Should().Be(ConversationState.Closed);
    }

    // Audit F3: a bot reply refused by the WhatsApp window is stored Failed by the real conversation service; neither
    // the live path nor the restart's reconcile counts it, so the row closes.
    [Fact]
    public async Task InactivityPass_ShouldCloseTheChat_WhenTheOnlyNewerMessageIsASendRefusedByTheWhatsAppWindow()
    {
        await using var env = await Env.CreateAsync(_postgres);
        var id = await env.SeedConversationAsync(ChannelType.WhatsApp, clock: Now.AddHours(-80));
        await env.InsertOldBinaryMessageAsync(id, MessageDirection.Inbound, MessageDeliveryStatus.Delivered, Now.AddHours(-80));

        env.Clock.UtcNow.Returns(Now.AddHours(-1));
        var refused = await env.ConversationService.SendMessageAsync(
            EntityId.From(id), new TenantId(Tenant), new MessageEnvelope([new TextBlock("seguimos?")]),
            EntityId.From("bot-1"), ConversationOwnerKind.System, CancellationToken.None);
        refused.DeliveryStatus.Should().Be(MessageDeliveryStatus.Failed, "the 24 h window refused it before any connector");
        (await env.ClockAsync(id)).Should().Be(Now.AddHours(-80), "the live path does not advance the clock for a refusal");

        await env.StartWorkerAndRunAPassAsync(Now);

        (await env.ClockAsync(id)).Should().Be(Now.AddHours(-80), "the reconcile does not count a Failed Outbound message");
        (await env.StateAsync(id)).Should().Be(ConversationState.Closed);
    }

    // D9 as designed: a reply claimed and stored 1 h ago whose connector then failed keeps the clock it advanced; the
    // monotonic reconcile does not move it back, and the row stays open.
    [Fact]
    public async Task InactivityPass_ShouldKeepTheChatOpen_WhenAClaimedReplyFailedAtTheProviderBeforeTheRestart()
    {
        await using var env = await Env.CreateAsync(_postgres);
        var contactId = await env.SeedContactAsync(ChannelType.WebChat, "visitor-reconcile");
        var id = await env.SeedConversationAsync(ChannelType.WebChat, clock: Now.AddHours(-80), contactId: contactId);
        env.Connector.SendAsync(Arg.Any<OutboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(new SendResult(false, null, "provider_error", "upstream 500"));

        env.Clock.UtcNow.Returns(Now.AddHours(-1));
        var outcome = await env.ConversationService.TrySendMessageAsync(
            EntityId.From(id), new TenantId(Tenant), new MessageEnvelope([new TextBlock("hola")]),
            Agent, ConversationOwnerKind.Agent, templateId: null, CancellationToken.None);
        outcome.Message!.DeliveryStatus.Should().Be(MessageDeliveryStatus.Failed, "the connector failed after the claim");
        (await env.ClockAsync(id)).Should().Be(Now.AddHours(-1), "the send claim advanced the clock before the send");

        await env.StartWorkerAndRunAPassAsync(Now);

        (await env.ClockAsync(id)).Should().Be(Now.AddHours(-1), "the reconcile is monotonic: it never moves a clock back");
        (await env.StateAsync(id)).Should().Be(ConversationState.Active);
    }

    /// <summary>One Postgres container for the class; each test creates its own database.</summary>
    public sealed class PostgresContainer : IAsyncLifetime
    {
        private IContainer? _container;

        public string ConnectionStringFor(string database) =>
            $"Host={_container!.Hostname};Port={_container.GetMappedPublicPort(5432)};" +
            $"Database={database};Username=postgres;Password=postgres";

        public async Task InitializeAsync()
        {
            _container = new ContainerBuilder("postgres:18-alpine")
                .WithEnvironment("POSTGRES_PASSWORD", "postgres")
                .WithEnvironment("POSTGRES_DB", "postgres")
                .WithPortBinding(5432, true)
                // `-h 127.0.0.1` probes over TCP: the entrypoint first runs a socket-only temporary server.
                .WithWaitStrategy(Wait.ForUnixContainer()
                    .UntilCommandIsCompleted("pg_isready", "-U", "postgres", "-h", "127.0.0.1"))
                .Build();
            await _container.StartAsync();
        }

        public async Task DisposeAsync()
        {
            if (_container is not null) await _container.DisposeAsync();
        }
    }

    /// <summary>A migrated database, the real Postgres stores and conversation service, and the worker's fakes.</summary>
    private sealed class Env : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly NpgsqlDataSource _db;

        private Env(string connectionString)
        {
            ConnectionString = connectionString;
            var registry = Substitute.For<IChannelRegistry>();
            registry.GetConnector(Arg.Any<ChannelType>()).Returns(Connector);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDataProtection().SetApplicationName("Verbara.Platform.Api.Tests");
            services.AddPostgresStorage(connectionString);
            services.AddSwitchboard();
            services.AddSingleton(Clock);
            services.AddSingleton(EventBus);
            services.AddSingleton(registry);
            services.AddSingleton(Substitute.For<IConversationLifecycleService>());
            _services = services.BuildServiceProvider();
            _db = NpgsqlDataSource.Create(connectionString);
        }

        public string ConnectionString { get; }
        public IClock Clock { get; } = Substitute.For<IClock>();
        public IChannelConnector Connector { get; } = Substitute.For<IChannelConnector>();
        public PlatformEventBus EventBus { get; } = new();
        public IConversationStore Conversations => _services.GetRequiredService<IConversationStore>();
        public IConversationService ConversationService => _services.GetRequiredService<IConversationService>();

        public static async Task<Env> CreateAsync(PostgresContainer postgres)
        {
            var name = $"rec_{Guid.NewGuid():N}";
            await using (var admin = NpgsqlDataSource.Create(postgres.ConnectionStringFor("postgres")))
            await using (var cmd = admin.CreateCommand($"CREATE DATABASE {name}"))
                await cmd.ExecuteNonQueryAsync();

            var connectionString = postgres.ConnectionStringFor(name);
            DatabaseMigrationService.ApplyMigrations(connectionString);
            return new Env(connectionString);
        }

        /// <summary>Starts a new worker (a process start) over the real store and runs one pass at <paramref name="now"/>.</summary>
        public async Task StartWorkerAndRunAPassAsync(DateTimeOffset now)
        {
            Clock.UtcNow.Returns(now);
            var tenants = Substitute.For<ITenantStore>();
            tenants.GetAllActiveAsync(Arg.Any<CancellationToken>())
                .Returns([new Tenant { TenantId = Tenant, Name = Tenant, Status = TenantStatus.Active }]);
            var accountStatus = Substitute.For<IAgentAccountStatusLookup>();
            accountStatus.GetActiveUserIdsAsync(Arg.Any<TenantId>(), Arg.Any<IReadOnlyCollection<EntityId>>(), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult<IReadOnlySet<EntityId>>(new HashSet<EntityId>(call.Arg<IReadOnlyCollection<EntityId>>())));
            var switchboard = new ConversationSwitchboard(
                Conversations, Substitute.For<IAgentCapacityService>(), Substitute.For<IAgentStore>(), Clock, EventBus, accountStatus);
            var worker = new ConversationTimeoutWorker(
                Conversations, tenants, switchboard, EventBus, Clock, new ServiceHeartbeat(),
                Microsoft.Extensions.Options.Options.Create(new DistributionOptions()),
                NullLogger<ConversationTimeoutWorker>.Instance,
                conversationOptions: Microsoft.Extensions.Options.Options.Create(new ConversationOptions()));

            await worker.ProcessTimeoutsAsync(CancellationToken.None);
        }

        public async Task<string> SeedContactAsync(ChannelType channel, string address)
        {
            var contact = new Contact
            {
                ContactId = EntityId.From($"contact-{Guid.NewGuid():N}"),
                TenantId = new TenantId(Tenant),
                CreatedAt = Now.AddDays(-10),
            };
            contact.AddAddress(new ChannelAddress(channel, address));
            await _services.GetRequiredService<IContactStore>().SaveAsync(contact, CancellationToken.None);
            return contact.ContactId.Value;
        }

        /// <summary>An agent-held <c>Active</c> conversation whose clock is <paramref name="clock"/>, written by SQL.</summary>
        public async Task<string> SeedConversationAsync(ChannelType channel, DateTimeOffset clock, string? contactId = null)
        {
            var id = $"c-{Guid.NewGuid():N}";
            await ExecuteAsync(
                "INSERT INTO conversations (conversation_id, tenant_id, contact_id, channel, state, owner_kind, owner_id, " +
                "metadata, created_at, last_message_at) VALUES (@Id, @TenantId, @ContactId, @Channel, @State, @OwnerKind, " +
                "@OwnerId, '{}'::jsonb, @CreatedAt, @Clock)",
                new NpgsqlParameter("Id", NpgsqlDbType.Text) { Value = id },
                new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = Tenant },
                new NpgsqlParameter("ContactId", NpgsqlDbType.Text) { Value = contactId ?? $"contact-{Guid.NewGuid():N}" },
                new NpgsqlParameter("Channel", NpgsqlDbType.Integer) { Value = (int)channel },
                new NpgsqlParameter("State", NpgsqlDbType.Integer) { Value = (int)ConversationState.Active },
                new NpgsqlParameter("OwnerKind", NpgsqlDbType.Integer) { Value = (int)ConversationOwnerKind.Agent },
                new NpgsqlParameter("OwnerId", NpgsqlDbType.Text) { Value = Agent.Value },
                new NpgsqlParameter("CreatedAt", NpgsqlDbType.TimestampTz) { Value = clock.AddHours(-20).UtcDateTime },
                new NpgsqlParameter("Clock", NpgsqlDbType.TimestampTz) { Value = clock.UtcDateTime });
            return id;
        }

        /// <summary>A message stored the way an older binary did: one INSERT, the conversation row untouched.</summary>
        public Task InsertOldBinaryMessageAsync(
            string conversationId, MessageDirection direction, MessageDeliveryStatus status, DateTimeOffset createdAt) =>
            ExecuteAsync(
                "INSERT INTO messages (message_id, conversation_id, tenant_id, direction, channel, content, delivery_status, " +
                "created_at) VALUES (@MessageId, @ConversationId, @TenantId, @Direction, 1, '{\"blocks\": []}'::jsonb, @Status, @CreatedAt)",
                new NpgsqlParameter("MessageId", NpgsqlDbType.Text) { Value = $"m-{Guid.NewGuid():N}" },
                new NpgsqlParameter("ConversationId", NpgsqlDbType.Text) { Value = conversationId },
                new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = Tenant },
                new NpgsqlParameter("Direction", NpgsqlDbType.Integer) { Value = (int)direction },
                new NpgsqlParameter("Status", NpgsqlDbType.Integer) { Value = (int)status },
                new NpgsqlParameter("CreatedAt", NpgsqlDbType.TimestampTz) { Value = createdAt.UtcDateTime });

        public async Task<DateTimeOffset?> ClockAsync(string conversationId) =>
            await ScalarAsync(
                "SELECT last_message_at FROM conversations WHERE tenant_id = @TenantId AND conversation_id = @Id",
                new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = Tenant },
                new NpgsqlParameter("Id", NpgsqlDbType.Text) { Value = conversationId }) is DateTime dt
                ? new DateTimeOffset(dt.ToUniversalTime())
                : null;

        public async Task<ConversationState> StateAsync(string conversationId) =>
            (ConversationState)(int)(await ScalarAsync(
                "SELECT state FROM conversations WHERE tenant_id = @TenantId AND conversation_id = @Id",
                new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = Tenant },
                new NpgsqlParameter("Id", NpgsqlDbType.Text) { Value = conversationId }))!;

        public async Task<object?> ScalarAsync(string sql, params NpgsqlParameter[] parameters)
        {
            await using var cmd = _db.CreateCommand(sql);
            cmd.Parameters.AddRange(parameters);
            return await cmd.ExecuteScalarAsync();
        }

        private async Task ExecuteAsync(string sql, params NpgsqlParameter[] parameters)
        {
            await using var cmd = _db.CreateCommand(sql);
            cmd.Parameters.AddRange(parameters);
            await cmd.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await _services.DisposeAsync();
            await _db.DisposeAsync();
            EventBus.Dispose();
        }
    }
}
