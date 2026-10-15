using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Core.Webhooks;
using Verbara.Platform.Storage.Postgres;

namespace Verbara.Platform.Api.Tests.Webhooks;

/// <summary>
/// One Testcontainers Postgres shared by every outbound-webhook delivery test that needs real row semantics
/// (webhook-delivery-runs-once, design D9). The tests of the collection run one after the other, and each
/// starts from <see cref="WebhookDeliveryPostgresFixture.ResetAsync"/>.
/// </summary>
[CollectionDefinition(Name)]
public sealed class WebhookDeliveryPostgresSuite : ICollectionFixture<WebhookDeliveryPostgresFixture>
{
    public const string Name = "Outbound webhook delivery on Postgres";
}

/// <summary>
/// A clean Postgres with the REAL embedded <c>Storage.Postgres</c> migrations applied in ordinal order (as
/// <c>Storage.Postgres.Tests/Stores/MigrationsFixture.cs</c> and the production runner apply them), the real
/// <see cref="IWebhookDeliveryStore"/> and <see cref="IWebhookSubscriptionStore"/> resolved from
/// <c>AddPostgresStorage(dataSource)</c>, and one seeded active subscription whose circuit is closed.
/// </summary>
public sealed class WebhookDeliveryPostgresFixture : IAsyncLifetime
{
    public const string TenantId = "tenant-webhook-claims";
    public const string SubscriptionId = "sub-webhook-claims";
    public const string EventType = WebhookEventTypes.ConversationMessage;

    /// <summary>A whole-second instant, so a timestamp round-trips through <c>timestamptz</c> unchanged.</summary>
    public static readonly DateTimeOffset Epoch = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private IContainer? _container;
    private ServiceProvider? _services;

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public IWebhookDeliveryStore DeliveryStore { get; private set; } = null!;

    public IWebhookSubscriptionStore SubscriptionStore { get; private set; } = null!;

    /// <summary>The seeded subscription: active, circuit closed, an <c>https://</c> endpoint.</summary>
    public static WebhookSubscription Subscription { get; } = new(
        SubscriptionId: SubscriptionId,
        TenantId: TenantId,
        Name: "Delivery claims receiver",
        EndpointUrl: "https://receiver.example.test/webhooks",
        Secret: "whsec-delivery-claims-test",
        EventTypes: [EventType],
        IsActive: true,
        CreatedAt: Epoch,
        UpdatedAt: Epoch,
        CircuitStatus: CircuitStatus.Closed);

    public async Task InitializeAsync()
    {
        _container = new ContainerBuilder("postgres:16-alpine")
            // A unique name: other sessions share this Docker daemon.
            .WithName($"verbara-webhook-delivery-pg-{Guid.NewGuid():N}")
            .WithEnvironment("POSTGRES_PASSWORD", "postgres")
            .WithEnvironment("POSTGRES_DB", "postgres")
            .WithPortBinding(5432, true)
            // `-h 127.0.0.1` probes over TCP: the image's entrypoint first runs a socket-only temporary
            // server, so a socket probe greens before 5432 accepts connections (spec live-db-fixture-readiness).
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilCommandIsCompleted("pg_isready", "-U", "postgres", "-h", "127.0.0.1"))
            .Build();
        await _container.StartAsync();

        DataSource = NpgsqlDataSource.Create(
            $"Host={_container.Hostname};Port={_container.GetMappedPublicPort(5432)};" +
            "Database=postgres;Username=postgres;Password=postgres");

        await ApplyMigrationsAsync();

        _services = new ServiceCollection().AddPostgresStorage(DataSource).BuildServiceProvider();
        DeliveryStore = _services.GetRequiredService<IWebhookDeliveryStore>();
        SubscriptionStore = _services.GetRequiredService<IWebhookSubscriptionStore>();

        await ResetAsync();
    }

    public async Task DisposeAsync()
    {
        if (_services is not null) await _services.DisposeAsync();
        if (DataSource is not null) await DataSource.DisposeAsync();
        if (_container is not null) await _container.DisposeAsync();
    }

    /// <summary>
    /// Empties both webhook tables and re-seeds <see cref="Subscription"/>, so a test never sees another
    /// test's deliveries in a poll, nor a circuit another test opened.
    /// </summary>
    public async Task ResetAsync(CancellationToken ct = default)
    {
        await using (var conn = await DataSource.OpenConnectionAsync(ct))
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM webhook_deliveries; DELETE FROM webhook_subscriptions;";
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await SubscriptionStore.SaveAsync(Subscription, ct);
    }

    /// <summary>
    /// A <c>Pending</c> delivery, due at <paramref name="nextRetryAt"/>, for the seeded subscription or for
    /// <paramref name="subscriptionId"/>.
    /// </summary>
    public static WebhookDelivery PendingDelivery(
        DateTimeOffset nextRetryAt, int attempts = 0, string subscriptionId = SubscriptionId)
    {
        var deliveryId = Guid.NewGuid().ToString("N");
        return new WebhookDelivery(
            DeliveryId: deliveryId,
            TenantId: TenantId,
            SubscriptionId: subscriptionId,
            EventType: EventType,
            Payload: $$"""{"id":"{{deliveryId}}","type":"{{EventType}}"}""",
            Status: WebhookDeliveryStatus.Pending,
            Attempts: attempts,
            MaxAttempts: 8,
            NextRetryAt: nextRetryAt,
            LastResponseCode: null,
            LastError: null,
            CreatedAt: nextRetryAt,
            DeliveredAt: null);
    }

    /// <summary>
    /// The two claim columns of one delivery, read raw: they are not part of <see cref="WebhookDelivery"/> nor of
    /// the delivery API (design D1). Both are <see langword="null"/> when the delivery holds no claim.
    /// </summary>
    public async Task<(string? ClaimId, DateTimeOffset? ExpiresAt)> ClaimColumnsAsync(
        string deliveryId, CancellationToken ct = default)
    {
        await using var conn = await DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT claim_id, claim_expires_at FROM webhook_deliveries WHERE delivery_id = @Id", conn);
        cmd.Parameters.Add(new NpgsqlParameter("Id", NpgsqlDbType.Varchar) { Value = deliveryId });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        (await reader.ReadAsync(ct)).Should().BeTrue($"delivery {deliveryId} exists");
        return (
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : new DateTimeOffset(reader.GetDateTime(1), TimeSpan.Zero));
    }

    // Mirrors DatabaseMigrationService and MigrationsFixture: every embedded Migrations/*.sql resource of the
    // Storage.Postgres assembly, ordered ordinally by name, applied in order.
    private async Task ApplyMigrationsAsync()
    {
        var assembly = typeof(Verbara.Platform.Storage.Postgres.ServiceCollectionExtensions).Assembly;
        const string prefix = "Verbara.Platform.Storage.Postgres.Migrations.";

        var resources = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        resources.Should().NotBeEmpty("the Storage.Postgres assembly embeds its migrations");

        await using var conn = await DataSource.OpenConnectionAsync();
        foreach (var resourceName in resources)
        {
            await using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Embedded migration {resourceName} could not be opened.");
            using var reader = new StreamReader(stream);
            var sql = await reader.ReadToEndAsync();

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
