using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Verbara.Platform.Core.Notifications;
using Verbara.Sdk.Push.Events;

namespace Verbara.Platform.Core.Tests.Push;

/// <summary>
/// <see cref="PlatformEvent.Metadata"/> is one stable instance per event, because the delivery filter reads it twice for
/// every event and subscriber of a stream (contact-history-opens-by-assignment, cell T-2 of task 0.3). The rest of the
/// file pins what caching must not change: its content, a copy made with <c>with</c>, and record equality.
/// </summary>
public sealed class PlatformEventMetadataTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 4, 10, 0, 0, 512, TimeSpan.Zero);

    private static NotificationEvent Notification() => new(
        "acme", "ticket", At, "n-1", "user-1", NotificationCategory.System, NotificationSeverity.Info, "Title", "Body", null);

    [Fact]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "A test enumerates the assembly; it is never trimmed.")]
    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "A test enumerates the assembly; it is never trimmed.")]
    public void Metadata_ShouldReturnOneInstance_WhenEveryConcreteEventIsReadTwice()
    {
        var types = typeof(PlatformEvent).Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(PlatformEvent)) && !t.IsAbstract)
            .ToList();

        // Every event of the assembly, so a new record that overrides Metadata with a computed value fails here. A
        // constructor is not needed: the getters read fields only, and a null field stays null in the metadata.
        types.Should().HaveCountGreaterThan(20);
        foreach (var type in types)
        {
            var evt = (PlatformEvent)RuntimeHelpers.GetUninitializedObject(type);

            evt.Metadata.Should().BeSameAs(evt.Metadata, because: $"{type.Name} is read twice per event and subscriber");
        }
    }

    [Fact]
    public void Metadata_ShouldCarryTheTenantAndNoUser_WhenTheEventIsATenantBroadcast()
    {
        var evt = new ConversationStateChangedEvent("acme", "c-1", "Active", "Resolved") { Timestamp = At };

        evt.Metadata.Should().Be(new PushEventMetadata("acme", UserId: null, At, CorrelationId: null));
        evt.Metadata.Should().Be(new PushEventMetadata("acme", UserId: null, At, CorrelationId: null),
            because: "the second read returns what the first built");
    }

    [Fact]
    public void Metadata_ShouldCarryTheRecipient_WhenTheEventTargetsAUser()
    {
        Notification().Metadata.Should().Be(new PushEventMetadata("acme", "user-1", At, CorrelationId: null));

        var revoked = new UserAccessRevokedEvent("acme", "user-2", "deleted") { Timestamp = At };
        revoked.Metadata.Should().Be(new PushEventMetadata("acme", "user-2", At, CorrelationId: null));
    }

    [Fact]
    public async Task Metadata_ShouldReturnOneInstance_WhenSubscribersReadTheFirstValueConcurrently()
    {
        const int Readers = 8;

        for (var round = 0; round < 200; round++)
        {
            var evt = new ConversationStateChangedEvent("acme", $"c-{round}", "Active", "Resolved");
            using var start = new Barrier(Readers);

            var reads = await Task.WhenAll(Enumerable.Range(0, Readers).Select(_ => Task.Factory.StartNew(
                () =>
                {
                    start.SignalAndWait();
                    return evt.Metadata;
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default)));

            reads.Should().AllSatisfy(read => read.Should().BeSameAs(reads[0]));
        }
    }

    [Fact]
    public void Metadata_ShouldFollowTheCopy_WhenAnEventAlreadyReadIsCopiedWithAnotherTenantOrTime()
    {
        var evt = new ConversationStateChangedEvent("acme", "c-1", "Active", "Resolved") { Timestamp = At };
        var read = evt.Metadata;

        var copy = evt with { TenantId = "other", Timestamp = At.AddMinutes(1) };

        copy.Metadata.Should().Be(new PushEventMetadata("other", UserId: null, At.AddMinutes(1), CorrelationId: null));
        evt.Metadata.Should().BeSameAs(read);
    }

    [Fact]
    public void Metadata_ShouldFollowTheCopy_WhenATargetedEventAlreadyReadIsCopiedWithAnotherUser()
    {
        var evt = Notification();
        _ = evt.Metadata;

        (evt with { UserId = "user-9" }).Metadata.UserId.Should().Be("user-9");
    }

    [Fact]
    public void Metadata_ShouldStayComputedFromTheFields_WhenABackplaneCopySetsItThroughTheBaseInit()
    {
        // RemoteEventDispatcher does exactly this on a decoded event: `decoded with { Metadata = envelope.Metadata }`. The
        // override has always ignored that value, so routing follows the event's own fields; caching must not adopt it.
        PushEvent evt = new ConversationStateChangedEvent("acme", "c-1", "Active", "Resolved") { Timestamp = At };

        var copy = evt with { Metadata = new PushEventMetadata("other", "user-9", At.AddDays(1), "corr", "topic", "trace") };

        copy.Metadata.Should().Be(new PushEventMetadata("acme", UserId: null, At, CorrelationId: null));
    }

    [Fact]
    public void Equals_ShouldNotDependOnTheCache_WhenOnlyOneOfTwoEqualEventsWasRead()
    {
        var read = new ConversationStateChangedEvent("acme", "c-1", "Active", "Resolved") { Timestamp = At };
        var unread = new ConversationStateChangedEvent("acme", "c-1", "Active", "Resolved") { Timestamp = At };
        _ = read.Metadata;

        read.Should().Be(unread);
        read.GetHashCode().Should().Be(unread.GetHashCode());
    }

    [Fact]
    public void Equals_ShouldStillTellEventsApart_WhenTheBaseFieldsDiffer()
    {
        var evt = new ConversationStateChangedEvent("acme", "c-1", "Active", "Resolved") { Timestamp = At };

        evt.Should().NotBe(evt with { TenantId = "other" });
        evt.Should().NotBe(evt with { Timestamp = At.AddTicks(1) });
        evt.Should().NotBe(evt with { Type = "other.type" });
        evt.Should().NotBe(evt with { NewState = "Closed" });
    }

    [Fact]
    public void ToString_ShouldPrintTheMetadataAndNoCacheMember_WhenTheEventWasRead()
    {
        var evt = new ConversationStateChangedEvent("acme", "c-1", "Active", "Resolved") { Timestamp = At };
        var before = evt.ToString();
        _ = evt.Metadata;

        evt.ToString().Should().Be(before).And.Contain("Metadata = PushEventMetadata");
    }
}
