using NSubstitute;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Audit;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Surveys;
using Verbara.Sdk.Pro.CsatRunner.Domain;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// csat-one-rating-per-dispatch (#697) — the voice capture sink follows the <see cref="ISurveyResponseStore.SaveAsync"/>
/// contract the HTTP capture endpoint follows: a rating the store did not keep publishes no
/// <see cref="CsatResponseRecordedEvent"/> and writes no audit row.
/// </summary>
public sealed class CsatVoiceCaptureSinkAdapterTests
{
    private static readonly TenantId Tenant = new("ten-42");
    private static readonly EntityId ConversationId = EntityId.From("conv-voice-1");

    [Fact]
    public async Task SubmitAsync_ShouldPublishNothingAndWriteNoAudit_WhenStoreKeepsNoRating()
    {
        var (sink, store, audit, published) = Build(saved: false);

        await sink.SubmitAsync(Capture(), CancellationToken.None);

        await store.Received(1).SaveAsync(Arg.Any<SurveyResponse>(), Arg.Any<CancellationToken>());
        Assert.Empty(published);
        Assert.Empty(audit.ReceivedCalls());
    }

    [Fact]
    public async Task SubmitAsync_ShouldPublishEventAndWriteAudit_WhenStoreKeepsRating()
    {
        var (sink, _, audit, published) = Build(saved: true);

        await sink.SubmitAsync(Capture(), CancellationToken.None);

        Assert.Single(published);
        await audit.Received(1).RecordAsync(
            Tenant, "csat", "csat.response.recorded", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<AuditChanges?>(),
            Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
    }

    private static (CsatVoiceCaptureSinkAdapter Sink, ISurveyResponseStore Store, IAuditService Audit,
        List<CsatResponseRecordedEvent> Published) Build(bool saved)
    {
        var conversations = Substitute.For<IConversationStore>();
        conversations.FindByIdAcrossTenantsAsync(ConversationId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Conversation?>(new Conversation
            {
                ConversationId = ConversationId,
                TenantId = Tenant,
                ContactId = EntityId.From("contact-1"),
                Channel = ChannelType.Voice,
                State = ConversationState.Closed,
                Owner = ConversationOwner.ForQueue(EntityId.From("queue-support")),
                CreatedAt = DateTimeOffset.UtcNow,
            }));

        var store = Substitute.For<ISurveyResponseStore>();
        store.SaveAsync(Arg.Any<SurveyResponse>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(saved));

        var audit = Substitute.For<IAuditService>();
        var bus = new PlatformEventBus();
        var published = new List<CsatResponseRecordedEvent>();
        bus.Events.Subscribe(e =>
        {
            if (e is CsatResponseRecordedEvent recorded)
                published.Add(recorded);
        });

        return (new CsatVoiceCaptureSinkAdapter(conversations, store, bus, audit), store, audit, published);
    }

    private static CsatCapture Capture() => new()
    {
        Rating = 4,
        Comment = null,
        Channel = "voice",
        QueueName = "support-tier1",
        ConversationId = ConversationId.Value,
        SurveyId = "survey-csat-1",
        QuestionId = "q-csat-rating",
        CapturedAt = DateTimeOffset.UtcNow,
    };
}
