using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Storage.Postgres.Stores;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// Testcontainers-backed round-trip suite for the per-queue CSAT config columns
/// added by migration 016 (csat-runner Phase A). Reuses <see cref="MigrationsFixture"/>
/// so the store runs against the REAL embedded DDL. Each test uses a unique tenant id.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresQueueStoreCsatTests : IClassFixture<MigrationsFixture>
{
    private readonly PostgresQueueStore _store;

    public PostgresQueueStoreCsatTests(MigrationsFixture fixture)
        => _store = new PostgresQueueStore(fixture.DataSource);

    [Fact]
    public async Task SaveThenGet_ShouldRoundTripCsatConfig_WhenConfigured()
    {
        var tenant = new TenantId($"t-{Guid.NewGuid():N}");
        var queue = new Queue
        {
            QueueId = EntityId.New(),
            TenantId = tenant,
            Name = "Support",
            CreatedAt = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
            Csat = new CsatConfig(
                Enabled: true,
                PreferredChannel: "webchat",
                PromptTemplateId: EntityId.From("tpl-1"),
                SamplingRatePercent: 20),
        };

        await _store.SaveAsync(queue, CancellationToken.None);

        var loaded = await _store.GetByIdAsync(tenant, queue.QueueId, CancellationToken.None);
        loaded.Should().NotBeNull();
        loaded!.Csat.Should().NotBeNull();
        loaded.Csat!.Enabled.Should().BeTrue();
        loaded.Csat.PreferredChannel.Should().Be("webchat");
        loaded.Csat.PromptTemplateId.Should().Be(EntityId.From("tpl-1"));
        loaded.Csat.SamplingRatePercent.Should().Be(20);
    }

    [Fact]
    public async Task SaveThenGet_ShouldLoadCsatNull_WhenNeverConfigured()
    {
        var tenant = new TenantId($"t-{Guid.NewGuid():N}");
        var queue = new Queue
        {
            QueueId = EntityId.New(),
            TenantId = tenant,
            Name = "Sales",
            CreatedAt = DateTimeOffset.UtcNow,
            // Csat left null — mirrors a pre-migration row (csat_enabled defaults false).
        };

        await _store.SaveAsync(queue, CancellationToken.None);

        var loaded = await _store.GetByIdAsync(tenant, queue.QueueId, CancellationToken.None);
        loaded.Should().NotBeNull();
        loaded!.Csat.Should().BeNull();
    }

    [Fact]
    public async Task SaveThenGet_ShouldUpdateCsatConfig_WhenReSaved()
    {
        var tenant = new TenantId($"t-{Guid.NewGuid():N}");
        var queue = new Queue
        {
            QueueId = EntityId.New(),
            TenantId = tenant,
            Name = "Support",
            CreatedAt = DateTimeOffset.UtcNow,
            Csat = new CsatConfig(true, "webchat", null, 20),
        };
        await _store.SaveAsync(queue, CancellationToken.None);

        queue.Csat = new CsatConfig(true, "sms", null, 50);
        await _store.SaveAsync(queue, CancellationToken.None);

        var loaded = await _store.GetByIdAsync(tenant, queue.QueueId, CancellationToken.None);
        loaded!.Csat!.PreferredChannel.Should().Be("sms");
        loaded.Csat.SamplingRatePercent.Should().Be(50);
    }

    [Fact]
    public async Task SaveAsync_ShouldBeRejected_WhenSamplingRateOutOfRange()
    {
        var tenant = new TenantId($"t-{Guid.NewGuid():N}");
        var queue = new Queue
        {
            QueueId = EntityId.New(),
            TenantId = tenant,
            Name = "Bad",
            CreatedAt = DateTimeOffset.UtcNow,
            Csat = new CsatConfig(true, "webchat", null, 150),
        };

        var act = () => _store.SaveAsync(queue, CancellationToken.None);

        await act.Should().ThrowAsync<Npgsql.PostgresException>()
            .Where(e => e.SqlState == "23514"); // check_violation
    }
}
