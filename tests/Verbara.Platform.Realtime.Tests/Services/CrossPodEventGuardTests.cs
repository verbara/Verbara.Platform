using Verbara.Platform.Core;
using Verbara.Platform.Core.Push;
using Verbara.Platform.Realtime.Services;
using FluentAssertions;
using Xunit;

namespace Verbara.Platform.Realtime.Tests.Services;

public sealed class CrossPodEventGuardTests
{
    private static HashSet<Type> CrossPodEventTypes() =>
        typeof(PlatformEvent).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && t.IsAssignableTo(typeof(ICrossPodEvent)))
            .ToHashSet();

    [Fact]
    public void Dispatcher_ShouldHandleExactlyTheCrossPodEventSet()
    {
        RemoteEventDispatcher.HandledEventTypes.Should().BeEquivalentTo(
            CrossPodEventTypes(),
            "the dispatcher must decode exactly the ICrossPodEvent set — no orphan handler, no missing case");
    }

    [Fact]
    public void EveryCrossPodEvent_ShouldBeRegisteredInPlatformPushJsonContext()
    {
        var missing = CrossPodEventTypes()
            .Where(t => PlatformPushJsonContext.Default.GetTypeInfo(t) is null)
            .Select(t => t.Name).OrderBy(n => n).ToList();

        missing.Should().BeEmpty(
            "every ICrossPodEvent must be in PlatformPushJsonContext for the Redis backplane");
    }

    [Fact]
    public void PlatformPushJsonContext_ShouldRegisterExactlyTheCrossPodEvents()
    {
        // The push context also registers the RemotePushEvent envelope (not a PlatformEvent);
        // restrict the comparison to PlatformEvent subtypes.
        var pushRegisteredEvents = typeof(PlatformEvent).Assembly.GetTypes()
            .Where(t => !t.IsAbstract
                && t.IsAssignableTo(typeof(PlatformEvent))
                && PlatformPushJsonContext.Default.GetTypeInfo(t) is not null)
            .ToHashSet();

        pushRegisteredEvents.Should().BeEquivalentTo(CrossPodEventTypes(),
            "PlatformPushJsonContext should register exactly the ICrossPodEvent PlatformEvents — " +
            "no unmarked event in the push context, no marked event missing from it");
    }
}
