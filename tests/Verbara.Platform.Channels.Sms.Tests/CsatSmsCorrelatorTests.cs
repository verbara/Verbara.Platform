using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Core;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Channels.Sms.Tests;

[Collection("CsatSmsCorrelator")]
public sealed class CsatSmsCorrelatorTests : IAsyncLifetime
{
    private readonly CsatSmsCorrelatorFixture _fixture;
    private NpgsqlDataSource _dataSource = null!;

    private static readonly TenantId Tenant = new("ten-42");
    private const string Phone = "+15551230000";

    public CsatSmsCorrelatorTests(CsatSmsCorrelatorFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await _fixture.ResetAsync();
        _dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
    }

    public async Task DisposeAsync() => await _dataSource.DisposeAsync();

    // ─── window logic ───────────────────────────────────────────────────────────

    [Fact]
    public async Task TryCorrelateAsync_ShouldCaptureAndConsume_WhenDigitReplyWithinWindow()
    {
        var now = new DateTimeOffset(2026, 07, 11, 12, 00, 00, TimeSpan.Zero);
        var clock = new FakeTimeProvider(now);
        var forwarder = new RecordingForwarder();
        await InsertDispatchAsync("disp-1", Phone, sentAt: now - TimeSpan.FromHours(1));

        var sut = new CsatSmsCorrelator(_dataSource, forwarder, clock);
        var consumed = await sut.TryCorrelateAsync(Tenant, Phone, "3", CancellationToken.None);

        consumed.Should().BeTrue();
        forwarder.Calls.Should().ContainSingle();
        forwarder.Calls[0].Rating.Should().Be(3);
        (await GetConsumedAtAsync("disp-1")).Should().NotBeNull();
    }

    [Fact]
    public async Task TryCorrelateAsync_ShouldFallThrough_WhenDispatchOlderThanWindow()
    {
        var now = new DateTimeOffset(2026, 07, 11, 12, 00, 00, TimeSpan.Zero);
        var clock = new FakeTimeProvider(now);
        var forwarder = new RecordingForwarder();
        // 25h old → outside the 24h window.
        await InsertDispatchAsync("disp-old", Phone, sentAt: now - TimeSpan.FromHours(25));

        var sut = new CsatSmsCorrelator(_dataSource, forwarder, clock);
        var consumed = await sut.TryCorrelateAsync(Tenant, Phone, "4", CancellationToken.None);

        consumed.Should().BeFalse();
        forwarder.Calls.Should().BeEmpty();
        (await GetConsumedAtAsync("disp-old")).Should().BeNull();
    }

    [Fact]
    public async Task TryCorrelateAsync_ShouldFallThrough_WhenNoDispatchExists()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 07, 11, 12, 00, 00, TimeSpan.Zero));
        var forwarder = new RecordingForwarder();

        var sut = new CsatSmsCorrelator(_dataSource, forwarder, clock);
        var consumed = await sut.TryCorrelateAsync(Tenant, Phone, "5", CancellationToken.None);

        consumed.Should().BeFalse();
        forwarder.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task TryCorrelateAsync_ShouldFallThrough_WhenDispatchAlreadyConsumed()
    {
        var now = new DateTimeOffset(2026, 07, 11, 12, 00, 00, TimeSpan.Zero);
        var clock = new FakeTimeProvider(now);
        var forwarder = new RecordingForwarder();
        await InsertDispatchAsync("disp-used", Phone, sentAt: now - TimeSpan.FromHours(1), consumedAt: now - TimeSpan.FromMinutes(5));

        var sut = new CsatSmsCorrelator(_dataSource, forwarder, clock);
        var consumed = await sut.TryCorrelateAsync(Tenant, Phone, "2", CancellationToken.None);

        consumed.Should().BeFalse();
        forwarder.Calls.Should().BeEmpty();
    }

    // ─── non-rating fall-through ─────────────────────────────────────────────────

    [Fact]
    public async Task TryCorrelateAsync_ShouldFallThroughWithoutConsuming_WhenBodyIsNotBareDigit()
    {
        var now = new DateTimeOffset(2026, 07, 11, 12, 00, 00, TimeSpan.Zero);
        var clock = new FakeTimeProvider(now);
        var forwarder = new RecordingForwarder();
        await InsertDispatchAsync("disp-1", Phone, sentAt: now - TimeSpan.FromHours(1));

        var sut = new CsatSmsCorrelator(_dataSource, forwarder, clock);
        var consumed = await sut.TryCorrelateAsync(Tenant, Phone, "Hello agent", CancellationToken.None);

        consumed.Should().BeFalse();
        forwarder.Calls.Should().BeEmpty();
        // The open dispatch MUST remain open — a non-rating message never consumes it.
        (await GetConsumedAtAsync("disp-1")).Should().BeNull();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("6")]
    [InlineData("12")]
    [InlineData("3 stars")]
    public async Task TryCorrelateAsync_ShouldFallThrough_WhenDigitOutsideOneToFiveOrNotBare(string body)
    {
        var now = new DateTimeOffset(2026, 07, 11, 12, 00, 00, TimeSpan.Zero);
        var clock = new FakeTimeProvider(now);
        var forwarder = new RecordingForwarder();
        await InsertDispatchAsync("disp-1", Phone, sentAt: now - TimeSpan.FromHours(1));

        var sut = new CsatSmsCorrelator(_dataSource, forwarder, clock);
        var consumed = await sut.TryCorrelateAsync(Tenant, Phone, body, CancellationToken.None);

        consumed.Should().BeFalse();
        forwarder.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData(" 4 ")]
    [InlineData("4\n")]
    public async Task TryCorrelateAsync_ShouldCapture_WhenDigitPaddedWithWhitespace(string body)
    {
        var now = new DateTimeOffset(2026, 07, 11, 12, 00, 00, TimeSpan.Zero);
        var clock = new FakeTimeProvider(now);
        var forwarder = new RecordingForwarder();
        await InsertDispatchAsync("disp-1", Phone, sentAt: now - TimeSpan.FromHours(1));

        var sut = new CsatSmsCorrelator(_dataSource, forwarder, clock);
        var consumed = await sut.TryCorrelateAsync(Tenant, Phone, body, CancellationToken.None);

        consumed.Should().BeTrue();
        forwarder.Calls.Should().ContainSingle().Which.Rating.Should().Be(4);
    }

    // ─── collision (most-recent wins) ────────────────────────────────────────────

    [Fact]
    public async Task TryCorrelateAsync_ShouldAttributeToMostRecentAndExpireOlder_WhenTwoDispatchesOpen()
    {
        var now = new DateTimeOffset(2026, 07, 11, 12, 00, 00, TimeSpan.Zero);
        var clock = new FakeTimeProvider(now);
        var forwarder = new RecordingForwarder();
        await InsertDispatchAsync("disp-older", Phone, surveyId: "srv-old", sentAt: now - TimeSpan.FromHours(10));
        await InsertDispatchAsync("disp-newer", Phone, surveyId: "srv-new", sentAt: now - TimeSpan.FromHours(1));

        var sut = new CsatSmsCorrelator(_dataSource, forwarder, clock);
        var consumed = await sut.TryCorrelateAsync(Tenant, Phone, "4", CancellationToken.None);

        consumed.Should().BeTrue();
        forwarder.Calls.Should().ContainSingle();
        forwarder.Calls[0].SurveyId.Should().Be("srv-new");
        // Both dispatches must be consumed: the winner captured, the older expired (no capture).
        (await GetConsumedAtAsync("disp-newer")).Should().NotBeNull();
        (await GetConsumedAtAsync("disp-older")).Should().NotBeNull();
    }

    // ─── helpers ────────────────────────────────────────────────────────────────

    private async Task InsertDispatchAsync(
        string dispatchId,
        string correlator,
        DateTimeOffset sentAt,
        string surveyId = "srv-csat-v1",
        DateTimeOffset? consumedAt = null)
    {
        await _dataSource.ExecuteAsync(
            "INSERT INTO csat_pending_dispatches " +
            "(dispatch_id, tenant_id, channel, correlator, survey_id, queue_name, conversation_id, sent_at, expires_at, consumed_at) " +
            "VALUES (@DispatchId, @TenantId, 'sms', @Correlator, @SurveyId, @QueueName, @ConversationId, @SentAt, @ExpiresAt, @ConsumedAt)",
            p =>
            {
                p.Add(new NpgsqlParameter("DispatchId", dispatchId));
                p.Add(new NpgsqlParameter("TenantId", Tenant.Value));
                p.Add(new NpgsqlParameter("Correlator", correlator));
                p.Add(new NpgsqlParameter("SurveyId", surveyId));
                p.Add(new NpgsqlParameter("QueueName", NpgsqlDbType.Text) { Value = "support-tier1" });
                p.Add(new NpgsqlParameter("ConversationId", NpgsqlDbType.Text) { Value = "conv-8f2a1c4e" });
                p.Add(new NpgsqlParameter("SentAt", NpgsqlDbType.TimestampTz) { Value = sentAt });
                p.Add(new NpgsqlParameter("ExpiresAt", NpgsqlDbType.TimestampTz) { Value = sentAt + TimeSpan.FromHours(24) });
                p.Add(new NpgsqlParameter("ConsumedAt", NpgsqlDbType.TimestampTz) { Value = (object?)consumedAt ?? DBNull.Value });
            },
            CancellationToken.None);
    }

    private async Task<DateTimeOffset?> GetConsumedAtAsync(string dispatchId)
    {
        return await _dataSource.QueryFirstOrDefaultAsync(
            "SELECT consumed_at FROM csat_pending_dispatches WHERE tenant_id = @TenantId AND dispatch_id = @DispatchId",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", Tenant.Value));
                p.Add(new NpgsqlParameter("DispatchId", dispatchId));
            },
            r => r.GetDateTimeOffsetOrNull("consumed_at"),
            CancellationToken.None);
    }

    private sealed class RecordingForwarder : ICsatCaptureForwarder
    {
        public List<(TenantId TenantId, string SurveyId, string QueueName, string ConversationId, int Rating, DateTimeOffset CapturedAt)> Calls { get; } = [];

        public Task ForwardSmsRatingAsync(
            TenantId tenantId, string surveyId, string queueName, string conversationId,
            int rating, DateTimeOffset capturedAt, CancellationToken ct)
        {
            Calls.Add((tenantId, surveyId, queueName, conversationId, rating, capturedAt));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FakeTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
