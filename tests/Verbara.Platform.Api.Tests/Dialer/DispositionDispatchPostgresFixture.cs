using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Api.Tests.Webhooks;
using Verbara.Platform.Core;
using Verbara.Platform.Storage.Postgres;
using Verbara.Platform.Typification;
using Verbara.Platform.Typification.Stores;
using Verbara.Sdk.Pro.Dialer.Campaign;

namespace Verbara.Platform.Api.Tests.Dialer;

/// <summary>Runs the dialer disposition Live-DB tests one after the other on one container.</summary>
[CollectionDefinition(Name)]
public sealed class DispositionDispatchPostgresSuite : ICollectionFixture<DispositionDispatchPostgresFixture>
{
    public const string Name = "Dialer disposition dispatch on Postgres";
}

/// <summary>
/// dialer-honours-dnc-callbacks-and-agent-dispositions (design D16, D17): a Testcontainers Postgres with the REAL
/// embedded <c>Storage.Postgres</c> migrations (as <see cref="WebhookDeliveryPostgresFixture"/>), the real submission,
/// dispatch, conversation and suggestion stores from <c>AddPostgresStorage(dataSource)</c>, the programmable
/// <see cref="InMemoryCampaignStore"/> as Pro's seam, a hand-moved clock, and the real
/// <see cref="DialerDispositionDispatcher"/> over them.
/// </summary>
public sealed class DispositionDispatchPostgresFixture : IAsyncLifetime
{
    public const string TenantId = "tenant-dialer-dispatch";

    /// <summary>A whole-second instant, so a timestamp round-trips through <c>timestamptz</c> unchanged.</summary>
    public static readonly DateTimeOffset Epoch = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private IContainer? _container;
    private ServiceProvider? _services;

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    /// <summary>The fixture's services: the Postgres stores, the seam fake, the clock, the event bus and the dispatcher.</summary>
    public IServiceProvider Services => _services!;

    internal MutableClock Clock { get; private set; } = null!;

    /// <summary>Pro's seam; replaced by <see cref="ResetAsync"/> so no test sees another's script or counts.</summary>
    internal InMemoryCampaignStore Campaigns { get; private set; } = new();

    public async Task InitializeAsync()
    {
        _container = new ContainerBuilder("postgres:16-alpine")
            .WithName($"verbara-dialer-dispatch-pg-{Guid.NewGuid():N}")
            .WithEnvironment("POSTGRES_PASSWORD", "postgres")
            .WithEnvironment("POSTGRES_DB", "postgres")
            .WithPortBinding(5432, true)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilCommandIsCompleted("pg_isready", "-U", "postgres", "-h", "127.0.0.1"))
            .Build();
        await _container.StartAsync();

        DataSource = NpgsqlDataSource.Create(
            $"Host={_container.Hostname};Port={_container.GetMappedPublicPort(5432)};" +
            "Database=postgres;Username=postgres;Password=postgres");
        await ApplyMigrationsAsync();

        Clock = new MutableClock(Epoch);
        _services = new ServiceCollection()
            .AddPostgresStorage(DataSource)
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Debug))
            .AddSingleton<IClock>(Clock)
            .AddSingleton<PlatformEventBus>()
            .AddSingleton<DialerDispositionMetrics>()
            .AddSingleton<DialerDispositionDispatcher>()
            .AddTransient<CampaignStoreBase>(_ => Campaigns)
            .BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        if (_services is not null) await _services.DisposeAsync();
        if (DataSource is not null) await DataSource.DisposeAsync();
        if (_container is not null) await _container.DisposeAsync();
    }

    /// <summary>Empties the dispatch, submission and conversation tables, resets the clock and the seam.</summary>
    public async Task ResetAsync()
    {
        await using (var conn = await DataSource.OpenConnectionAsync())
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "DELETE FROM dialer_disposition_dispatches; DELETE FROM typification_submissions; " +
                "DELETE FROM conversations; DELETE FROM typification_ai_suggestions;";
            await cmd.ExecuteNonQueryAsync();
        }

        Clock.UtcNow = Epoch;
        Campaigns = new InMemoryCampaignStore();
    }

    public DialerDispositionDispatcher Dispatcher => Services.GetRequiredService<DialerDispositionDispatcher>();

    public ITypificationSubmissionStore Submissions => Services.GetRequiredService<ITypificationSubmissionStore>();

    public IDialerDispositionDispatchStore Dispatches => Services.GetRequiredService<IDialerDispositionDispatchStore>();

    /// <summary>A worker over the fixture's services (it resolves its collaborators at each pass).</summary>
    public DialerDispositionDispatchWorker NewWorker() =>
        new(Services, Services.GetRequiredService<ILogger<DialerDispositionDispatchWorker>>());

    public static TypificationSubmission Submission(string conversationId, string leaf, SubmissionSource source = SubmissionSource.Manual) => new()
    {
        TenantId = new TenantId(TenantId),
        ConversationId = EntityId.From(conversationId),
        AgentId = EntityId.From("agent-1"),
        SchemaId = EntityId.From("schema-1"),
        SchemaVersion = 1,
        SelectedNodePath = [EntityId.From("root"), EntityId.From(leaf)],
        LeafNodeId = EntityId.From(leaf),
        FieldValues = new Dictionary<string, string>(),
        Source = source,
        CompletedAt = Epoch,
    };

    /// <summary>The dispatch a typification of <paramref name="conversationId"/> owes, pre-claimed as the request writes it.</summary>
    public DialerDispositionDispatch PreClaimed(string conversationId, long codeId = 7) =>
        Dispatcher.NewPreClaimed(new DialerDispositionDispatch
        {
            TenantId = TenantId,
            ConversationId = conversationId,
            CampaignId = 11,
            ActionId = $"action-{conversationId}",
            DialerContactId = 4711,
            DispositionCodeId = codeId,
            DispositionCode = "NOT-HOME",
            Comment = "note",
            RequestsCallback = false,
            AgentId = "agent-1",
        });

    /// <summary>The dispatch row of one Conversation, read raw.</summary>
    public async Task<(int Attempts, string? Result, DateTimeOffset NextAttemptAt, string? ClaimId, DateTimeOffset? PublishedAt)?>
        RowAsync(string conversationId)
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT attempts, result, next_attempt_at, claim_id, published_at FROM dialer_disposition_dispatches " +
            "WHERE tenant_id = @t AND conversation_id = @c", conn);
        cmd.Parameters.Add(new NpgsqlParameter("t", NpgsqlDbType.Text) { Value = TenantId });
        cmd.Parameters.Add(new NpgsqlParameter("c", NpgsqlDbType.Text) { Value = conversationId });
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return null;
        return (
            reader.GetInt32(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            new DateTimeOffset(reader.GetDateTime(2), TimeSpan.Zero),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : new DateTimeOffset(reader.GetDateTime(4), TimeSpan.Zero));
    }

    public async Task<long> CountAsync(string table, string conversationId)
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            $"SELECT COUNT(*) FROM {table} WHERE tenant_id = @t AND conversation_id = @c", conn);
        cmd.Parameters.Add(new NpgsqlParameter("t", NpgsqlDbType.Text) { Value = TenantId });
        cmd.Parameters.Add(new NpgsqlParameter("c", NpgsqlDbType.Text) { Value = conversationId });
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    // As WebhookDeliveryPostgresFixture: every embedded Migrations/*.sql of Storage.Postgres, in ordinal order.
    private async Task ApplyMigrationsAsync()
    {
        var assembly = typeof(Verbara.Platform.Storage.Postgres.ServiceCollectionExtensions).Assembly;
        const string prefix = "Verbara.Platform.Storage.Postgres.Migrations.";
        var resources = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        resources.Should().NotBeEmpty();

        await using var conn = await DataSource.OpenConnectionAsync();
        foreach (var resourceName in resources)
        {
            await using var stream = assembly.GetManifestResourceStream(resourceName)!;
            using var reader = new StreamReader(stream);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = await reader.ReadToEndAsync();
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
