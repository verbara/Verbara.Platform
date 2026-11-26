using Npgsql;
using Verbara.Platform.Core;
using Verbara.Platform.Storage.Postgres.Stores;
using Verbara.Platform.Surveys;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// csat-one-rating-per-dispatch (#697): an email or SMS survey dispatch yields at most one stored rating. The first
/// capture of a (tenant, survey, conversation, channel) is kept; a later or concurrent capture of the same dispatch
/// stores nothing and <see cref="PostgresSurveyResponseStore.SaveAsync"/> reports it. Webchat and voice captures are
/// not keyed. Migration 035 keeps the first rating of a dispatch that already holds several and runs again unchanged.
/// Live-DB (Testcontainers), every real migration applied.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SurveyResponseOnePerDispatchTests : IClassFixture<ConversationClockFixture>
{
    private const string Migration035 = "035_SurveyResponseOnePerDispatch.sql";
    private const string UniqueIndex = "ux_survey_resp_one_per_dispatch";

    private static readonly DateTimeOffset T0 = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

    private readonly ConversationClockFixture _fixture;

    public SurveyResponseOnePerDispatchTests(ConversationClockFixture fixture) => _fixture = fixture;

    private static SurveyResponse Capture(
        TenantId tenant, string surveyId, string conversationId, string channel, int rating, DateTimeOffset capturedAt) => new()
    {
        ResponseId = EntityId.New(),
        SurveyId = EntityId.From(surveyId),
        TenantId = tenant,
        ConversationId = EntityId.From(conversationId),
        ContactId = EntityId.From(conversationId),
        Answers = [new SurveyAnswer(EntityId.From(SurveyQuestionIds.CsatRating), rating.ToString(System.Globalization.CultureInfo.InvariantCulture))],
        SubmittedAt = capturedAt,
        Channel = channel,
        QueueName = "support",
        Rating = rating,
        CapturedAt = capturedAt,
    };

    private static async Task<IReadOnlyList<SurveyResponse>> RowsAsync(
        PostgresSurveyResponseStore store, TenantId tenant, string conversationId) =>
        await store.GetByConversationAsync(tenant, EntityId.From(conversationId), CancellationToken.None);

    [Theory]
    [InlineData("email")]
    [InlineData("sms")]
    public async Task SaveAsync_ShouldKeepOnlyTheFirstRating_WhenTheSameDispatchIsCapturedTwice(string channel)
    {
        var store = new PostgresSurveyResponseStore(await _fixture.CreateDatabaseAsync());
        var tenant = new TenantId("t-697");

        await store.SaveAsync(Capture(tenant, "srv-1", "conv-1", channel, 4, T0), CancellationToken.None);
        await store.SaveAsync(Capture(tenant, "srv-1", "conv-1", channel, 1, T0.AddMinutes(5)), CancellationToken.None);

        var rows = await RowsAsync(store, tenant, "conv-1");
        rows.Should().ContainSingle("one {0} dispatch yields one rating", channel)
            .Which.Rating.Should().Be(4, "the first reply wins");
    }

    [Theory]
    [InlineData("email")]
    [InlineData("sms")]
    public async Task SaveAsync_ShouldStoreOneRow_WhenCapturesOfOneDispatchRunConcurrently(string channel)
    {
        var store = new PostgresSurveyResponseStore(await _fixture.CreateDatabaseAsync());
        var tenant = new TenantId("t-697");
        var captures = Enumerable.Range(1, 8)
            .Select(i => Capture(tenant, "srv-1", "conv-c", channel, 1 + (i % 5), T0.AddSeconds(i)))
            .ToList();

        using var start = new ManualResetEventSlim(false);
        var tasks = captures
            .Select(c => Task.Run(async () =>
            {
                start.Wait();
                await store.SaveAsync(c, CancellationToken.None);
            }))
            .ToList();
        start.Set();
        await Task.WhenAll(tasks);

        (await RowsAsync(store, tenant, "conv-c")).Should().ContainSingle();
    }

    [Theory]
    [InlineData("email")]
    [InlineData("sms")]
    public async Task SaveAsync_ShouldReportWhetherTheRatingWasStored_WhenTheDispatchIsCapturedAgain(string channel)
    {
        var store = new PostgresSurveyResponseStore(await _fixture.CreateDatabaseAsync());
        var tenant = new TenantId("t-697");

        var first = await store.SaveAsync(Capture(tenant, "srv-1", "conv-r", channel, 5, T0), CancellationToken.None);
        var second = await store.SaveAsync(Capture(tenant, "srv-1", "conv-r", channel, 2, T0.AddMinutes(1)), CancellationToken.None);

        first.Should().BeTrue();
        second.Should().BeFalse();
    }

    [Fact]
    public async Task SaveAsync_ShouldReportOneStoredRating_WhenCapturesOfOneDispatchRunConcurrently()
    {
        var store = new PostgresSurveyResponseStore(await _fixture.CreateDatabaseAsync());
        var tenant = new TenantId("t-697");
        var captures = Enumerable.Range(1, 8)
            .Select(i => Capture(tenant, "srv-1", "conv-cc", "email", 1 + (i % 5), T0.AddSeconds(i)))
            .ToList();

        using var start = new ManualResetEventSlim(false);
        var tasks = captures
            .Select(c => Task.Run(async () =>
            {
                start.Wait();
                return await store.SaveAsync(c, CancellationToken.None);
            }))
            .ToList();
        start.Set();
        var stored = await Task.WhenAll(tasks);

        stored.Count(s => s).Should().Be(1);
        var row = (await RowsAsync(store, tenant, "conv-cc")).Single();
        captures.Single(c => c.ResponseId == row.ResponseId).Should().NotBeNull("the stored row is the capture reported as stored");
    }

    [Fact]
    public async Task SaveAsync_ShouldStoreEachRating_WhenDispatchesDiffer()
    {
        var store = new PostgresSurveyResponseStore(await _fixture.CreateDatabaseAsync());
        var tenant = new TenantId("t-697");
        var other = new TenantId("t-697-b");

        // Another conversation, another survey, another channel, another tenant: four other dispatches.
        (await store.SaveAsync(Capture(tenant, "srv-1", "conv-d", "email", 5, T0), CancellationToken.None)).Should().BeTrue();
        (await store.SaveAsync(Capture(tenant, "srv-1", "conv-e", "email", 4, T0), CancellationToken.None)).Should().BeTrue();
        (await store.SaveAsync(Capture(tenant, "srv-2", "conv-d", "email", 3, T0), CancellationToken.None)).Should().BeTrue();
        (await store.SaveAsync(Capture(tenant, "srv-1", "conv-d", "sms", 2, T0), CancellationToken.None)).Should().BeTrue();
        (await store.SaveAsync(Capture(other, "srv-1", "conv-d", "email", 1, T0), CancellationToken.None)).Should().BeTrue();

        (await RowsAsync(store, tenant, "conv-d")).Should().HaveCount(3);
        (await RowsAsync(store, tenant, "conv-e")).Should().ContainSingle();
        (await RowsAsync(store, other, "conv-d")).Should().ContainSingle();
    }

    [Theory]
    [InlineData("webchat")]
    [InlineData("voice")]
    public async Task SaveAsync_ShouldLeaveTokenChannelsUnkeyed_WhenTheSameConversationIsCapturedTwice(string channel)
    {
        // Webchat and voice captures carry no dispatch: this change keys only email and SMS (design D4).
        var store = new PostgresSurveyResponseStore(await _fixture.CreateDatabaseAsync());
        var tenant = new TenantId("t-697");

        (await store.SaveAsync(Capture(tenant, "srv-1", "conv-w", channel, 5, T0), CancellationToken.None)).Should().BeTrue();
        (await store.SaveAsync(Capture(tenant, "srv-1", "conv-w", channel, 3, T0.AddMinutes(1)), CancellationToken.None)).Should().BeTrue();

        (await RowsAsync(store, tenant, "conv-w")).Should().HaveCount(2);
    }

    [Fact]
    public async Task SaveAsync_ShouldReportNotStored_WhenTheSameResponseIdIsSavedAgain()
    {
        var store = new PostgresSurveyResponseStore(await _fixture.CreateDatabaseAsync());
        var tenant = new TenantId("t-697");
        var response = Capture(tenant, "srv-1", "conv-p", "webchat", 5, T0);

        (await store.SaveAsync(response, CancellationToken.None)).Should().BeTrue();
        (await store.SaveAsync(response, CancellationToken.None)).Should().BeFalse("the same response id is stored once");
    }

    // ── Migration 035 on a database that already holds several ratings for one dispatch ─────────

    private static Task InsertRawAsync(
        NpgsqlDataSource ds, string tenant, string responseId, string conversationId, string channel, int rating,
        DateTimeOffset capturedAt) =>
        ConversationClockFixture.ExecuteAsync(ds,
            "INSERT INTO survey_responses (response_id, survey_id, tenant_id, conversation_id, contact_id, answers, " +
            "submitted_at, channel, queue_name, rating, captured_at) VALUES (@ResponseId, 'srv-1', @TenantId, " +
            "@ConversationId, @ConversationId, '[]'::jsonb, @CapturedAt, @Channel, 'support', @Rating, @CapturedAt)",
            new NpgsqlParameter("ResponseId", responseId),
            new NpgsqlParameter("TenantId", tenant),
            new NpgsqlParameter("ConversationId", conversationId),
            new NpgsqlParameter("Channel", channel),
            new NpgsqlParameter("Rating", rating),
            new NpgsqlParameter("CapturedAt", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = capturedAt });

    private static async Task<List<string>> ResponseIdsAsync(NpgsqlDataSource ds)
    {
        var ids = new List<string>();
        await using var cmd = ds.CreateCommand("SELECT response_id FROM survey_responses ORDER BY response_id");
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) ids.Add(reader.GetString(0));
        return ids;
    }

    private static async Task<bool> IndexExistsAsync(NpgsqlDataSource ds) =>
        (long)(await ConversationClockFixture.ScalarAsync(ds,
            "SELECT COUNT(*) FROM pg_indexes WHERE indexname = @Name",
            new NpgsqlParameter("Name", UniqueIndex)))! == 1;

    [Fact]
    public async Task Migration035_ShouldKeepTheFirstRatingOfEachDispatch_WhenRunOnADatabaseThatHoldsSeveral()
    {
        var ds = await _fixture.CreateDatabaseBeforeAsync(Migration035);
        // Email dispatch with three replies (r-e2 first by time, though not first by id), an SMS dispatch with two,
        // the same conversation by email in another tenant, and token channels with repeats (left as they are).
        await InsertRawAsync(ds, "t-a", "r-e1", "conv-1", "email", 2, T0.AddMinutes(3));
        await InsertRawAsync(ds, "t-a", "r-e2", "conv-1", "email", 5, T0);
        await InsertRawAsync(ds, "t-a", "r-e3", "conv-1", "email", 1, T0.AddMinutes(9));
        await InsertRawAsync(ds, "t-a", "r-s1", "conv-2", "sms", 4, T0);
        await InsertRawAsync(ds, "t-a", "r-s2", "conv-2", "sms", 3, T0);
        await InsertRawAsync(ds, "t-b", "r-b1", "conv-1", "email", 3, T0.AddMinutes(1));
        await InsertRawAsync(ds, "t-a", "r-w1", "conv-3", "webchat", 5, T0);
        await InsertRawAsync(ds, "t-a", "r-w2", "conv-3", "webchat", 4, T0.AddMinutes(1));
        await InsertRawAsync(ds, "t-a", "r-v1", "conv-4", "voice", 5, T0);
        await InsertRawAsync(ds, "t-a", "r-v2", "conv-4", "voice", 4, T0.AddMinutes(1));

        await ConversationClockFixture.ApplyMigrationAsync(ds, Migration035);

        // Earliest capture kept; on a tie the lower response id.
        (await ResponseIdsAsync(ds)).Should().Equal("r-b1", "r-e2", "r-s1", "r-v1", "r-v2", "r-w1", "r-w2");
        (await IndexExistsAsync(ds)).Should().BeTrue();
    }

    [Fact]
    public async Task Migration035_ShouldLeaveTheSameRowsAndIndex_WhenReapplied()
    {
        var ds = await _fixture.CreateDatabaseAsync();
        var store = new PostgresSurveyResponseStore(ds);
        var tenant = new TenantId("t-697");
        await store.SaveAsync(Capture(tenant, "srv-1", "conv-1", "email", 4, T0), CancellationToken.None);
        var before = await ResponseIdsAsync(ds);

        await ConversationClockFixture.ApplyMigrationAsync(ds, Migration035);

        (await ResponseIdsAsync(ds)).Should().Equal(before);
        (await IndexExistsAsync(ds)).Should().BeTrue();
        (await store.SaveAsync(Capture(tenant, "srv-1", "conv-1", "email", 1, T0.AddMinutes(1)), CancellationToken.None))
            .Should().BeFalse("the key still holds after a second run");
    }

    [Fact]
    public async Task Migration035_ShouldRefuseASecondRatingForADispatch_WhenWrittenOutsideTheStore()
    {
        var ds = await _fixture.CreateDatabaseAsync();
        await InsertRawAsync(ds, "t-a", "r-1", "conv-1", "email", 4, T0);

        var second = () => InsertRawAsync(ds, "t-a", "r-2", "conv-1", "email", 2, T0.AddMinutes(1));

        (await second.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23505");
    }
}
