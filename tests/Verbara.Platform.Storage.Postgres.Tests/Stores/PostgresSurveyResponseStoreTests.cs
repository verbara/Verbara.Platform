using Verbara.Platform.Core;
using Verbara.Platform.Storage.Postgres.Stores;
using Verbara.Platform.Surveys;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// Testcontainers-backed round-trip suite for <see cref="PostgresSurveyResponseStore"/>
/// (csat-runner Phase A). Reuses <see cref="MigrationsFixture"/> so the store is exercised
/// against the REAL embedded migrations (incl. 016, which adds the 6 CSAT columns +
/// CHECK constraints + partial indexes). Each test uses a unique tenant id so no per-test
/// truncation is needed.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresSurveyResponseStoreTests : IClassFixture<MigrationsFixture>
{
    private readonly PostgresSurveyResponseStore _store;

    public PostgresSurveyResponseStoreTests(MigrationsFixture fixture)
        => _store = new PostgresSurveyResponseStore(fixture.DataSource);

    [Fact]
    public async Task SaveThenGet_ShouldRoundTripCsatColumns_WhenCsatFlavored()
    {
        var tenant = new TenantId($"t-{Guid.NewGuid():N}");
        var surveyId = EntityId.From($"srv-{Guid.NewGuid():N}");
        var conversationId = EntityId.From($"conv-{Guid.NewGuid():N}");
        var capturedAt = new DateTimeOffset(2026, 7, 7, 9, 15, 0, TimeSpan.Zero);

        var response = new SurveyResponse
        {
            ResponseId = EntityId.New(),
            SurveyId = surveyId,
            TenantId = tenant,
            ConversationId = conversationId,
            ContactId = EntityId.From("contact-1"),
            Answers = [new SurveyAnswer(EntityId.From(SurveyQuestionIds.CsatRating), "5")],
            SubmittedAt = capturedAt,
            Channel = "webchat",
            QueueName = "support-tier1",
            Rating = 5,
            Comment = "Fast and friendly, thanks!",
            CapturedAt = capturedAt,
        };

        await _store.SaveAsync(response, CancellationToken.None);

        var loaded = (await _store.GetBySurveyAsync(tenant, surveyId, CancellationToken.None)).Single();
        loaded.Channel.Should().Be("webchat");
        loaded.QueueName.Should().Be("support-tier1");
        loaded.Rating.Should().Be(5);
        loaded.Comment.Should().Be("Fast and friendly, thanks!");
        loaded.CapturedAt.Should().Be(capturedAt);
        loaded.CallId.Should().BeNull();
    }

    [Fact]
    public async Task SaveThenGet_ShouldLoadWithNullCsatColumns_WhenNonCsatRow()
    {
        var tenant = new TenantId($"t-{Guid.NewGuid():N}");
        var surveyId = EntityId.From($"srv-{Guid.NewGuid():N}");

        var response = new SurveyResponse
        {
            ResponseId = EntityId.New(),
            SurveyId = surveyId,
            TenantId = tenant,
            ConversationId = EntityId.From("conv-x"),
            ContactId = EntityId.From("contact-x"),
            Answers = [new SurveyAnswer(EntityId.From("q1"), "9")],
            SubmittedAt = DateTimeOffset.UtcNow,
            // No CSAT columns set — mirrors a pre-migration NPS/Custom row.
        };

        await _store.SaveAsync(response, CancellationToken.None);

        var loaded = (await _store.GetBySurveyAsync(tenant, surveyId, CancellationToken.None)).Single();
        loaded.Channel.Should().BeNull();
        loaded.QueueName.Should().BeNull();
        loaded.Rating.Should().BeNull();
        loaded.Comment.Should().BeNull();
        loaded.CapturedAt.Should().BeNull();
        loaded.CallId.Should().BeNull();
        loaded.Answers.Should().ContainSingle();
    }

    [Fact]
    public async Task GetByQueueAndChannelAsync_ShouldReturnMatchingRows_WhenInRange()
    {
        var tenant = new TenantId($"t-{Guid.NewGuid():N}");
        var surveyId = EntityId.From($"srv-{Guid.NewGuid():N}");
        var queue = $"q-{Guid.NewGuid():N}";
        var t = new DateTimeOffset(2026, 7, 7, 9, 0, 0, TimeSpan.Zero);

        await _store.SaveAsync(Csat(tenant, surveyId, queue, "webchat", 5, t), CancellationToken.None);
        await _store.SaveAsync(Csat(tenant, surveyId, queue, "webchat", 3, t.AddMinutes(1)), CancellationToken.None);
        // Different channel — must be excluded.
        await _store.SaveAsync(Csat(tenant, surveyId, queue, "sms", 4, t.AddMinutes(2)), CancellationToken.None);

        var range = new DateRange(t.AddHours(-1), t.AddHours(1));
        var rows = await _store.GetByQueueAndChannelAsync(tenant, queue, "webchat", range, CancellationToken.None);

        rows.Should().HaveCount(2);
        rows.Should().OnlyContain(r => r.Channel == "webchat");
        // Ordered captured_at DESC.
        rows[0].Rating.Should().Be(3);
        rows[1].Rating.Should().Be(5);
    }

    [Fact]
    public async Task Insert_ShouldBeRejected_WhenRatingOutOfRange()
    {
        var tenant = new TenantId($"t-{Guid.NewGuid():N}");
        var bad = Csat(tenant, EntityId.From("srv-bad"), "q", "webchat", 6, DateTimeOffset.UtcNow);

        var act = () => _store.SaveAsync(bad, CancellationToken.None);

        await act.Should().ThrowAsync<Npgsql.PostgresException>()
            .Where(e => e.SqlState == "23514"); // check_violation
    }

    [Fact]
    public async Task Insert_ShouldBeRejected_WhenChannelInvalid()
    {
        var tenant = new TenantId($"t-{Guid.NewGuid():N}");
        var bad = Csat(tenant, EntityId.From("srv-bad2"), "q", "carrier-pigeon", 5, DateTimeOffset.UtcNow);

        var act = () => _store.SaveAsync(bad, CancellationToken.None);

        await act.Should().ThrowAsync<Npgsql.PostgresException>()
            .Where(e => e.SqlState == "23514");
    }

    private static SurveyResponse Csat(
        TenantId tenant, EntityId surveyId, string queue, string channel, int rating, DateTimeOffset capturedAt) => new()
    {
        ResponseId = EntityId.New(),
        SurveyId = surveyId,
        TenantId = tenant,
        ConversationId = EntityId.New(),
        ContactId = EntityId.From("contact-c"),
        Answers = [],
        SubmittedAt = capturedAt,
        Channel = channel,
        QueueName = queue,
        Rating = rating,
        CapturedAt = capturedAt,
    };
}
