using Verbara.Platform.Core;
using Verbara.Platform.Storage.Postgres.Stores;
using Verbara.Platform.Surveys;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// Testcontainers-backed suite for <see cref="PostgresSurveyAnalytics.GetByQueueAndChannelAsync"/>
/// (csat-runner Phase A) — the DB-side COUNT/AVG aggregate served by the partial index
/// idx_survey_resp_queue_captured. Reuses <see cref="MigrationsFixture"/> + the real
/// <see cref="PostgresSurveyResponseStore"/> to seed rows. Each test uses a unique tenant id.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresSurveyAnalyticsTests : IClassFixture<MigrationsFixture>
{
    private readonly PostgresSurveyResponseStore _responseStore;
    private readonly PostgresSurveyAnalytics _analytics;

    public PostgresSurveyAnalyticsTests(MigrationsFixture fixture)
    {
        _responseStore = new PostgresSurveyResponseStore(fixture.DataSource);
        var surveyStore = new PostgresSurveyStore(fixture.DataSource);
        _analytics = new PostgresSurveyAnalytics(fixture.DataSource, _responseStore, surveyStore);
    }

    [Fact]
    public async Task GetByQueueAndChannelAsync_ShouldAverageRatings_WhenRowsInRange()
    {
        var tenant = new TenantId($"t-{Guid.NewGuid():N}");
        var queue = $"q-{Guid.NewGuid():N}";
        var t = new DateTimeOffset(2026, 7, 7, 9, 0, 0, TimeSpan.Zero);

        await _responseStore.SaveAsync(Csat(tenant, queue, "webchat", 5, t), CancellationToken.None);
        await _responseStore.SaveAsync(Csat(tenant, queue, "webchat", 4, t.AddMinutes(1)), CancellationToken.None);
        await _responseStore.SaveAsync(Csat(tenant, queue, "webchat", 3, t.AddMinutes(2)), CancellationToken.None);
        // Different channel — excluded from the webchat aggregate.
        await _responseStore.SaveAsync(Csat(tenant, queue, "sms", 1, t.AddMinutes(3)), CancellationToken.None);

        var range = new DateRange(t.AddHours(-1), t.AddHours(1));
        var summary = await _analytics.GetByQueueAndChannelAsync(tenant, queue, "webchat", range, CancellationToken.None);

        summary.TotalResponses.Should().Be(3);
        summary.AverageScore.Should().BeApproximately(4.0, 0.001);
        summary.NpsScore.Should().BeNull();
    }

    [Fact]
    public async Task GetByQueueAndChannelAsync_ShouldReturnZero_WhenNoRowsInRange()
    {
        var tenant = new TenantId($"t-{Guid.NewGuid():N}");
        var range = new DateRange(
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero));

        var summary = await _analytics.GetByQueueAndChannelAsync(tenant, "no-queue", "webchat", range, CancellationToken.None);

        summary.TotalResponses.Should().Be(0);
        summary.AverageScore.Should().Be(0d);
    }

    // ─── csat-completion — scope-wide aggregate (GetScopeAggregateAsync) ─────────

    [Fact]
    public async Task GetScopeAggregateAsync_ShouldRollUpPerQueueAndScope_WhenRowsInRange()
    {
        var tenant = new TenantId($"t-{Guid.NewGuid():N}");
        var t = new DateTimeOffset(2026, 7, 7, 9, 0, 0, TimeSpan.Zero);

        // support-tier1: 4 + 5 = avg 4.5 over 2; billing: 3 + 5 = avg 4.0 over 2 → scope 4 over avg 4.25.
        await _responseStore.SaveAsync(Csat(tenant, "support-tier1", "webchat", 4, t), CancellationToken.None);
        await _responseStore.SaveAsync(Csat(tenant, "support-tier1", "voice", 5, t.AddMinutes(1)), CancellationToken.None);
        await _responseStore.SaveAsync(Csat(tenant, "billing", "webchat", 3, t.AddMinutes(2)), CancellationToken.None);
        await _responseStore.SaveAsync(Csat(tenant, "billing", "voice", 5, t.AddMinutes(3)), CancellationToken.None);

        var range = new DateRange(t.AddHours(-1), t.AddHours(1));
        var scope = await _analytics.GetScopeAggregateAsync(tenant, channel: null, range, CancellationToken.None);

        scope.TotalResponses.Should().Be(4);
        scope.AverageRating.Should().BeApproximately(4.25, 0.001);
        scope.Queues.Should().HaveCount(2);
        scope.Queues.Should().ContainSingle(q => q.QueueName == "support-tier1" && q.TotalResponses == 2)
            .Which.AverageRating.Should().BeApproximately(4.5, 0.001);
        scope.Queues.Should().ContainSingle(q => q.QueueName == "billing" && q.TotalResponses == 2)
            .Which.AverageRating.Should().BeApproximately(4.0, 0.001);
    }

    [Fact]
    public async Task GetScopeAggregateAsync_ShouldFilterToChannel_WhenChannelSupplied()
    {
        var tenant = new TenantId($"t-{Guid.NewGuid():N}");
        var t = new DateTimeOffset(2026, 7, 7, 9, 0, 0, TimeSpan.Zero);

        await _responseStore.SaveAsync(Csat(tenant, "support-tier1", "voice", 4, t), CancellationToken.None);
        await _responseStore.SaveAsync(Csat(tenant, "support-tier1", "webchat", 2, t.AddMinutes(1)), CancellationToken.None);

        var range = new DateRange(t.AddHours(-1), t.AddHours(1));
        var scope = await _analytics.GetScopeAggregateAsync(tenant, "voice", range, CancellationToken.None);

        // Only the voice row counts.
        scope.TotalResponses.Should().Be(1);
        scope.AverageRating.Should().BeApproximately(4.0, 0.001);
        scope.Queues.Should().ContainSingle(q => q.QueueName == "support-tier1" && q.TotalResponses == 1);
    }

    [Fact]
    public async Task GetScopeAggregateAsync_ShouldReturnEmpty_WhenNoRowsInRange()
    {
        var tenant = new TenantId($"t-{Guid.NewGuid():N}");
        var range = new DateRange(
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero));

        var scope = await _analytics.GetScopeAggregateAsync(tenant, channel: null, range, CancellationToken.None);

        scope.TotalResponses.Should().Be(0);
        scope.AverageRating.Should().Be(0d);
        scope.Queues.Should().BeEmpty();
    }

    private static SurveyResponse Csat(
        TenantId tenant, string queue, string channel, int rating, DateTimeOffset capturedAt) => new()
    {
        ResponseId = EntityId.New(),
        SurveyId = EntityId.From("srv-csat-v1"),
        TenantId = tenant,
        ConversationId = EntityId.New(),
        ContactId = EntityId.From("contact-a"),
        Answers = [],
        SubmittedAt = capturedAt,
        Channel = channel,
        QueueName = queue,
        Rating = rating,
        CapturedAt = capturedAt,
    };
}
