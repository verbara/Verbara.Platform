using Verbara.Platform.Core.Push;

namespace Verbara.Platform.Core.Tests.Push;

public sealed class LiveConnectionRegistryTests
{
    [Fact]
    public void AbortAll_ShouldAbortEveryConnectionOfTheUserAndNoOther_WhenSeveralUsersHoldConnections()
    {
        var registry = new LiveConnectionRegistry();
        var aborted = new List<string>();
        using var a1 = registry.Register("acme", "alice", () => aborted.Add("alice-1"));
        using var a2 = registry.Register("acme", "alice", () => aborted.Add("alice-2"));
        using var b = registry.Register("acme", "bob", () => aborted.Add("bob"));
        using var otherTenant = registry.Register("globex", "alice", () => aborted.Add("globex-alice"));

        var count = registry.AbortAll("acme", "alice");

        count.Should().Be(2);
        aborted.Should().BeEquivalentTo("alice-1", "alice-2");
    }

    [Fact]
    public void AbortAll_ShouldNotAbortAConnection_WhenItAlreadyEnded()
    {
        var registry = new LiveConnectionRegistry();
        var aborted = 0;
        var registration = registry.Register("acme", "alice", () => aborted++);
        registration.Dispose();

        registry.AbortAll("acme", "alice").Should().Be(0);
        aborted.Should().Be(0);
    }

    [Fact]
    public void AbortAll_ShouldReturnZero_WhenCalledAgainForTheSameUser()
    {
        // The same revocation can arrive twice (typed re-publish + backplane envelope); the second
        // delivery must be a no-op.
        var registry = new LiveConnectionRegistry();
        var aborted = 0;
        using var registration = registry.Register("acme", "alice", () => aborted++);

        registry.AbortAll("acme", "alice").Should().Be(1);
        registry.AbortAll("acme", "alice").Should().Be(0);
        aborted.Should().Be(1);
    }

    [Fact]
    public void AbortAll_ShouldStillAbortTheOthers_WhenOneConnectionWasDisposedConcurrently()
    {
        var registry = new LiveConnectionRegistry();
        var aborted = new List<string>();
        using var gone = registry.Register("acme", "alice", () => throw new ObjectDisposedException("stream"));
        using var live = registry.Register("acme", "alice", () => aborted.Add("live"));

        var act = () => registry.AbortAll("acme", "alice");

        act.Should().NotThrow(because: "a connection torn down mid-abort has already reached the goal");
        aborted.Should().ContainSingle().Which.Should().Be("live");
    }

    [Fact]
    public void AbortAll_ShouldAbortEveryOtherConnectionAndThenThrow_WhenOneAbortFails()
    {
        var registry = new LiveConnectionRegistry();
        var aborted = new List<string>();
        using var broken = registry.Register("acme", "alice", () => throw new InvalidOperationException("transport failed"));
        using var live = registry.Register("acme", "alice", () => aborted.Add("live"));

        var act = () => registry.AbortAll("acme", "alice");

        act.Should().Throw<AggregateException>().WithInnerException<InvalidOperationException>();
        aborted.Should().ContainSingle(because: "one failing abort must not shield the account's other connections");
        registry.AbortAll("acme", "alice").Should().Be(0, because: "the failed entry is forgotten too, not left registered");
    }

    [Fact]
    public void Register_ShouldTrackANewConnection_WhenThePreviousOnesWereAborted()
    {
        var registry = new LiveConnectionRegistry();
        using var first = registry.Register("acme", "alice", () => { });
        registry.AbortAll("acme", "alice");
        var abortedAgain = false;

        using var second = registry.Register("acme", "alice", () => abortedAgain = true);

        registry.AbortAll("acme", "alice").Should().Be(1);
        abortedAgain.Should().BeTrue();
    }

    [Fact]
    public void Dispose_ShouldBeIdempotent_WhenTheRegistrationIsDisposedTwice()
    {
        var registry = new LiveConnectionRegistry();
        var registration = registry.Register("acme", "alice", () => { });

        registration.Dispose();
        var act = () => registration.Dispose();

        act.Should().NotThrow();
    }
}
