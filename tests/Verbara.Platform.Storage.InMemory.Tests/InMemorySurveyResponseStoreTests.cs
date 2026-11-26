using Verbara.Platform.Core;
using Verbara.Platform.Storage.InMemory;
using Verbara.Platform.Surveys;

namespace Verbara.Platform.Storage.InMemory.Tests;

/// <summary>
/// csat-one-rating-per-dispatch (#697): the in-memory store keeps the rule of the Postgres one, so an email or SMS
/// dispatch (tenant, conversation, survey, channel) yields one stored rating, the first; webchat and voice are not
/// keyed by dispatch.
/// </summary>
public sealed class InMemorySurveyResponseStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

    private static SurveyResponse Capture(
        string tenant, string surveyId, string conversationId, string channel, int rating) => new()
    {
        ResponseId = EntityId.New(),
        SurveyId = EntityId.From(surveyId),
        TenantId = new TenantId(tenant),
        ConversationId = EntityId.From(conversationId),
        ContactId = EntityId.From(conversationId),
        Answers = [],
        SubmittedAt = T0,
        Channel = channel,
        QueueName = "support",
        Rating = rating,
        CapturedAt = T0,
    };

    [Theory]
    [InlineData("email")]
    [InlineData("sms")]
    public async Task SaveAsync_ShouldKeepTheFirstRating_WhenTheSameDispatchIsCapturedTwice(string channel)
    {
        var store = new InMemorySurveyResponseStore();

        (await store.SaveAsync(Capture("t-1", "srv-1", "conv-1", channel, 4), CancellationToken.None)).Should().BeTrue();
        (await store.SaveAsync(Capture("t-1", "srv-1", "conv-1", channel, 1), CancellationToken.None)).Should().BeFalse();

        (await store.GetByConversationAsync(new TenantId("t-1"), EntityId.From("conv-1"), CancellationToken.None))
            .Should().ContainSingle().Which.Rating.Should().Be(4);
    }

    [Fact]
    public async Task SaveAsync_ShouldStoreEachRating_WhenDispatchesDiffer()
    {
        var store = new InMemorySurveyResponseStore();

        (await store.SaveAsync(Capture("t-1", "srv-1", "conv-1", "email", 5), CancellationToken.None)).Should().BeTrue();
        (await store.SaveAsync(Capture("t-1", "srv-1", "conv-1", "sms", 4), CancellationToken.None)).Should().BeTrue();
        (await store.SaveAsync(Capture("t-1", "srv-2", "conv-1", "email", 3), CancellationToken.None)).Should().BeTrue();
        (await store.SaveAsync(Capture("t-1", "srv-1", "conv-2", "email", 2), CancellationToken.None)).Should().BeTrue();
        (await store.SaveAsync(Capture("t-2", "srv-1", "conv-1", "email", 1), CancellationToken.None)).Should().BeTrue();

        (await store.GetByConversationAsync(new TenantId("t-1"), EntityId.From("conv-1"), CancellationToken.None))
            .Should().HaveCount(3);
    }

    [Theory]
    [InlineData("webchat")]
    [InlineData("voice")]
    public async Task SaveAsync_ShouldLeaveTokenChannelsUnkeyed_WhenTheSameConversationIsCapturedTwice(string channel)
    {
        var store = new InMemorySurveyResponseStore();

        (await store.SaveAsync(Capture("t-1", "srv-1", "conv-1", channel, 5), CancellationToken.None)).Should().BeTrue();
        (await store.SaveAsync(Capture("t-1", "srv-1", "conv-1", channel, 3), CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task SaveAsync_ShouldReportNotStored_WhenTheSameResponseIdIsSavedAgain()
    {
        var store = new InMemorySurveyResponseStore();
        var response = Capture("t-1", "srv-1", "conv-1", "webchat", 5);

        (await store.SaveAsync(response, CancellationToken.None)).Should().BeTrue();
        (await store.SaveAsync(response, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task SaveAsync_ShouldStoreOneRating_WhenCapturesOfOneDispatchRunConcurrently()
    {
        var store = new InMemorySurveyResponseStore();
        var captures = Enumerable.Range(1, 16).Select(i => Capture("t-1", "srv-1", "conv-c", "email", 1 + (i % 5))).ToList();

        var stored = await Task.WhenAll(captures.Select(c => Task.Run(() => store.SaveAsync(c, CancellationToken.None))));

        stored.Count(s => s).Should().Be(1);
        (await store.GetByConversationAsync(new TenantId("t-1"), EntityId.From("conv-c"), CancellationToken.None))
            .Should().ContainSingle();
    }
}
