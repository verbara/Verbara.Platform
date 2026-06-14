using Verbara.Platform.Core;
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
}
